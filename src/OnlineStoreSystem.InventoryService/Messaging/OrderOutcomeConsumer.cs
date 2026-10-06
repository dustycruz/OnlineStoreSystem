// File: src/OnlineStoreSystem.InventoryService/Messaging/OrderOutcomeConsumer.cs
using Microsoft.Extensions.Options;
using OnlineStoreSystem.InventoryService.Data.Entities;
using OnlineStoreSystem.InventoryService.Services;
using OnlineStoreSystem.Shared.Contracts.Inventory;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Events;
using OnlineStoreSystem.Shared.Identifiers;
using OnlineStoreSystem.Shared.Messaging;

namespace OnlineStoreSystem.InventoryService.Messaging;

/// <summary>
/// Reacts to the outcome of the saga (queue <c>inventory.order-outcomes</c>, DLQ <c>inventory.order-outcomes.dlq</c>):
/// <list type="bullet">
///   <item><see cref="PaymentProcessedEvent"/> → <b>commit</b> the reservation (stock leaves on-hand).</item>
///   <item><see cref="PaymentFailedEvent"/> → <b>ReleaseStock compensation</b>, reply <see cref="StockReleasedEvent"/>.</item>
///   <item><see cref="OrderCancelledEvent"/> → ReleaseStock compensation, reply <see cref="StockReleasedEvent"/>.</item>
/// </list>
/// Uses the same ProcessedMessages idempotency guard and manual ACK / DLQ semantics as <see cref="OrderPlacedConsumer"/>.
/// </summary>
public sealed class OrderOutcomeConsumer(
    IRabbitMqConnectionProvider connectionProvider,
    IServiceScopeFactory scopeFactory,
    ICorrelationIdAccessor correlationIdAccessor,
    IOptions<RabbitMqOptions> options,
    ILogger<OrderOutcomeConsumer> logger)
    : RabbitMqConsumerBackgroundService(connectionProvider, scopeFactory, correlationIdAccessor, options, logger)
{
    private static readonly IReadOnlyDictionary<string, Type> MessageTypes = new Dictionary<string, Type>
    {
        [nameof(PaymentProcessedEvent)] = typeof(PaymentProcessedEvent),
        [nameof(PaymentFailedEvent)] = typeof(PaymentFailedEvent),
        [nameof(OrderCancelledEvent)] = typeof(OrderCancelledEvent)
    };

    protected override string QueueName => MessagingTopology.InventoryOrderOutcomesQueue;

    protected override IReadOnlyDictionary<string, Type> SupportedMessageTypes => MessageTypes;

    protected override async Task HandleAsync(
        IntegrationEvent message,
        MessageContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var idempotencyGuard = services.GetRequiredService<ProcessedMessagesIdempotencyGuard>();
        var reservations = services.GetRequiredService<StockReservationService>();
        var publisher = services.GetRequiredService<IEventPublisher>();

        switch (message)
        {
            case PaymentProcessedEvent paymentProcessed:
                EnsureOrderId(paymentProcessed.OrderId, message);
                var committed = await idempotencyGuard.ExecuteOnceAsync(
                    context,
                    process: token => reservations.CommitAsync(paymentProcessed.OrderId, token),
                    onDuplicate: _ => Task.FromResult<IReadOnlyList<StockReservation>>([]),
                    cancellationToken);
                Logger.LogInformation("Payment {PaymentId} processed: committed {Count} reservation(s) of order {OrderId}",
                    paymentProcessed.PaymentId, committed.Count, paymentProcessed.OrderId);
                break;

            case PaymentFailedEvent paymentFailed:
                EnsureOrderId(paymentFailed.OrderId, message);
                Logger.LogWarning("COMPENSATION: payment failed for order {OrderId} ({Reason}) - releasing reserved stock",
                    paymentFailed.OrderId, paymentFailed.Reason);
                await ReleaseAndReplyAsync(paymentFailed, paymentFailed.OrderId, context, idempotencyGuard, reservations,
                    publisher, cancellationToken);
                break;

            case OrderCancelledEvent orderCancelled:
                EnsureOrderId(orderCancelled.OrderId, message);
                Logger.LogWarning("COMPENSATION: order {OrderId} cancelled ({Reason}) - releasing reserved stock",
                    orderCancelled.OrderId, orderCancelled.Reason);
                await ReleaseAndReplyAsync(orderCancelled, orderCancelled.OrderId, context, idempotencyGuard, reservations,
                    publisher, cancellationToken);
                break;

            default:
                throw new PoisonMessageException($"{message.MessageType} is not handled by {ConsumerName}.");
        }
    }

    private async Task ReleaseAndReplyAsync(
        IntegrationEvent trigger,
        Guid orderId,
        MessageContext context,
        ProcessedMessagesIdempotencyGuard idempotencyGuard,
        StockReservationService reservations,
        IEventPublisher publisher,
        CancellationToken cancellationToken)
    {
        var (released, isReplay) = await idempotencyGuard.ExecuteOnceAsync(
            context,
            process: async token => (await reservations.ReleaseAsync(orderId, token), false),
            onDuplicate: async token =>
            {
                var all = await reservations.GetReservationsAsync(orderId, token);
                IReadOnlyList<StockReservation> previouslyReleased =
                    all.Where(r => r.Status == ReservationStatuses.Released).ToList();
                return (previouslyReleased, true);
            },
            cancellationToken);

        var reply = new StockReleasedEvent
        {
            EventId = DeterministicGuid.Create(trigger.EventId, nameof(StockReleasedEvent)),
            CorrelationId = context.CorrelationId,
            OrderId = orderId,
            Items = released.Select(r => new ReleasedStockLine(r.ProductId, r.Quantity)).ToList(),
            TriggeredBy = trigger.MessageType,
            IsReplay = isReplay
        };

        await publisher.PublishAsync(reply, RoutingKeys.StockReleased, cancellationToken);
    }

    private static void EnsureOrderId(Guid orderId, IntegrationEvent message)
    {
        if (orderId == Guid.Empty)
        {
            throw new PoisonMessageException($"{message.MessageType} {message.EventId} has an empty OrderId.");
        }
    }
}
