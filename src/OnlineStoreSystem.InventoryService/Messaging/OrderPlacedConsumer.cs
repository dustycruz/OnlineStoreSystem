// File: src/OnlineStoreSystem.InventoryService/Messaging/OrderPlacedConsumer.cs
using Microsoft.Extensions.Options;
using OnlineStoreSystem.InventoryService.Services;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Events;
using OnlineStoreSystem.Shared.Identifiers;
using OnlineStoreSystem.Shared.Messaging;

namespace OnlineStoreSystem.InventoryService.Messaging;

/// <summary>
/// Saga participant for the <b>Reserve Stock</b> step.
/// <para>Queue <c>inventory.order-placed</c> (bound to <c>order.placed</c>; dead-letters to
/// <c>inventory.order-placed.dlq</c> via <c>onlinestore.dlx</c>).</para>
/// For every <see cref="OrderPlacedEvent"/>:
/// <list type="number">
///   <item>Validate the payload - an invalid message is poison → <c>BasicNack(requeue:false)</c> → DLQ.</item>
///   <item>Idempotency: check <c>ProcessedMessages</c> for (MessageId, Consumer); a redelivery does not reserve twice.</item>
///   <item>Reserve all lines or none, and insert the ProcessedMessages row, in ONE transaction.</item>
///   <item>Publish <see cref="StockReservedEvent"/> or <see cref="StockReservationFailedEvent"/> and wait for the
///         publisher confirm.</item>
///   <item>Only then does the base class send <c>BasicAck</c>. If anything fails before the ACK the broker
///         redelivers, step 2 recognises the message and step 4 re-sends the same reply (same EventId).</item>
/// </list>
/// </summary>
public sealed class OrderPlacedConsumer(
    IRabbitMqConnectionProvider connectionProvider,
    IServiceScopeFactory scopeFactory,
    ICorrelationIdAccessor correlationIdAccessor,
    IOptions<RabbitMqOptions> options,
    ILogger<OrderPlacedConsumer> logger)
    : RabbitMqConsumerBackgroundService(connectionProvider, scopeFactory, correlationIdAccessor, options, logger)
{
    private static readonly IReadOnlyDictionary<string, Type> MessageTypes = new Dictionary<string, Type>
    {
        [nameof(OrderPlacedEvent)] = typeof(OrderPlacedEvent)
    };

    protected override string QueueName => MessagingTopology.InventoryOrderPlacedQueue;

    protected override IReadOnlyDictionary<string, Type> SupportedMessageTypes => MessageTypes;

    protected override async Task HandleAsync(
        IntegrationEvent message,
        MessageContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var orderPlaced = (OrderPlacedEvent)message;
        Validate(orderPlaced);

        var idempotencyGuard = services.GetRequiredService<ProcessedMessagesIdempotencyGuard>();
        var reservations = services.GetRequiredService<StockReservationService>();
        var publisher = services.GetRequiredService<IEventPublisher>();

        var lines = orderPlaced.Items.Select(item => (item.ProductId, item.Quantity)).ToList();

        Logger.LogInformation("Reserving stock for order {OrderId} ({OrderNumber}): {LineCount} line(s)",
            orderPlaced.OrderId, orderPlaced.OrderNumber, lines.Count);

        var reply = await idempotencyGuard.ExecuteOnceAsync<Reply?>(
            context,
            process: async token =>
            {
                var result = await reservations.ReserveAsync(orderPlaced.OrderId, lines, context.CorrelationId, token);
                return BuildReply(orderPlaced, result, context, isReplay: false);
            },
            onDuplicate: token => BuildReplayAsync(orderPlaced, lines, reservations, context, token),
            cancellationToken);

        if (reply is null)
        {
            Logger.LogInformation("No reply needed for duplicate OrderPlacedEvent of order {OrderId}", orderPlaced.OrderId);
            return;
        }

        await publisher.PublishAsync(reply.Event, reply.RoutingKey, cancellationToken);
    }

    private static void Validate(OrderPlacedEvent orderPlaced)
    {
        if (orderPlaced.OrderId == Guid.Empty)
        {
            throw new PoisonMessageException("OrderPlacedEvent.OrderId is empty.");
        }

        if (orderPlaced.Items is null || orderPlaced.Items.Count == 0)
        {
            throw new PoisonMessageException($"OrderPlacedEvent for order {orderPlaced.OrderId} has no items.");
        }

        var invalid = orderPlaced.Items.FirstOrDefault(i => i.ProductId <= 0 || i.Quantity <= 0);
        if (invalid is not null)
        {
            throw new PoisonMessageException(
                $"OrderPlacedEvent for order {orderPlaced.OrderId} contains an invalid line (product {invalid.ProductId}, quantity {invalid.Quantity}).");
        }
    }

    private static Reply BuildReply(OrderPlacedEvent orderPlaced, ReserveStockResult result, MessageContext context, bool isReplay)
    {
        if (result.Status is ReserveStockStatus.Reserved or ReserveStockStatus.AlreadyReserved)
        {
            return new Reply(
                new StockReservedEvent
                {
                    EventId = DeterministicGuid.Create(orderPlaced.EventId, nameof(StockReservedEvent)),
                    CorrelationId = context.CorrelationId,
                    OrderId = orderPlaced.OrderId,
                    Items = result.Lines,
                    IsReplay = isReplay
                },
                RoutingKeys.StockReserved);
        }

        return new Reply(
            new StockReservationFailedEvent
            {
                EventId = DeterministicGuid.Create(orderPlaced.EventId, nameof(StockReservationFailedEvent)),
                CorrelationId = context.CorrelationId,
                OrderId = orderPlaced.OrderId,
                Reason = $"Insufficient stock: {StockReservationService.Describe(result.Shortages)}",
                Shortages = result.Shortages,
                IsReplay = isReplay
            },
            RoutingKeys.StockReservationFailed);
    }

    /// <summary>
    /// Redelivery of an already processed message: rebuild the reply from the persisted state so a reply
    /// lost before the ACK is sent again (same deterministic EventId). Nothing is reserved twice.
    /// </summary>
    private async Task<Reply?> BuildReplayAsync(
        OrderPlacedEvent orderPlaced,
        IReadOnlyList<(int ProductId, int Quantity)> lines,
        StockReservationService reservations,
        MessageContext context,
        CancellationToken cancellationToken)
    {
        var existing = await reservations.GetReservationsAsync(orderPlaced.OrderId, cancellationToken);

        if (existing.Count > 0 && existing.All(r => r.Status == Shared.Contracts.Inventory.ReservationStatuses.Released))
        {
            Logger.LogInformation("Order {OrderId} reservations were already released (saga compensated); not replaying",
                orderPlaced.OrderId);
            return null;
        }

        if (existing.Count > 0)
        {
            var lineItems = existing
                .Select(r => new ReservedStockLine(r.ProductId, r.Quantity, StockReservationService.Available(r.InventoryItem)))
                .ToList();
            return BuildReply(orderPlaced,
                new ReserveStockResult(ReserveStockStatus.AlreadyReserved, existing, lineItems, []),
                context, isReplay: true);
        }

        var shortages = await reservations.FindShortagesAsync(lines, cancellationToken);
        return BuildReply(orderPlaced,
            new ReserveStockResult(ReserveStockStatus.InsufficientStock, [], [], shortages),
            context, isReplay: true);
    }

    private sealed record Reply(IntegrationEvent Event, string RoutingKey);
}
