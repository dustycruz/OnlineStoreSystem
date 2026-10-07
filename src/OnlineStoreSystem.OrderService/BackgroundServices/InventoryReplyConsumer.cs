// File: src/OnlineStoreSystem.OrderService/BackgroundServices/InventoryReplyConsumer.cs
using Microsoft.Extensions.Options;
using OnlineStoreSystem.OrderService.Saga;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Events;
using OnlineStoreSystem.Shared.Messaging;

namespace OnlineStoreSystem.OrderService.BackgroundServices;

/// <summary>
/// Feeds InventoryService's replies back into the saga orchestrator.
/// Queue <c>order.inventory-replies</c> (bound to <c>inventory.stock-reserved</c>,
/// <c>inventory.stock-reservation-failed</c>, <c>inventory.stock-released</c>; DLQ
/// <c>order.inventory-replies.dlq</c>). Manual ACK / NACK semantics come from the shared base class;
/// idempotency comes from the saga state machine (a reply is applied only in the matching step).
/// </summary>
public sealed class InventoryReplyConsumer(
    IRabbitMqConnectionProvider connectionProvider,
    IServiceScopeFactory scopeFactory,
    ICorrelationIdAccessor correlationIdAccessor,
    IOptions<RabbitMqOptions> options,
    ILogger<InventoryReplyConsumer> logger)
    : RabbitMqConsumerBackgroundService(connectionProvider, scopeFactory, correlationIdAccessor, options, logger)
{
    private static readonly IReadOnlyDictionary<string, Type> MessageTypes = new Dictionary<string, Type>
    {
        [nameof(StockReservedEvent)] = typeof(StockReservedEvent),
        [nameof(StockReservationFailedEvent)] = typeof(StockReservationFailedEvent),
        [nameof(StockReleasedEvent)] = typeof(StockReleasedEvent)
    };

    protected override string QueueName => RabbitMqConfiguration.OrderInventoryRepliesQueue;

    protected override IReadOnlyDictionary<string, Type> SupportedMessageTypes => MessageTypes;

    protected override Task HandleAsync(
        IntegrationEvent message,
        MessageContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var orchestrator = services.GetRequiredService<OrderSagaOrchestrator>();

        return message switch
        {
            StockReservedEvent reserved when reserved.OrderId != Guid.Empty =>
                orchestrator.HandleStockReservedAsync(reserved, cancellationToken),
            StockReservationFailedEvent failed when failed.OrderId != Guid.Empty =>
                orchestrator.HandleStockReservationFailedAsync(failed, cancellationToken),
            StockReleasedEvent released when released.OrderId != Guid.Empty =>
                orchestrator.HandleStockReleasedAsync(released, cancellationToken),
            _ => throw new PoisonMessageException($"{message.MessageType} {message.EventId} has no usable OrderId.")
        };
    }
}
