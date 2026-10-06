// File: src/OnlineStoreSystem.Shared/Messaging/MessagingTopology.cs
namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>AMQP routing keys used on the <c>onlinestore.events</c> topic exchange.</summary>
public static class RoutingKeys
{
    public const string OrderPlaced = "order.placed";
    public const string OrderCancelled = "order.cancelled";

    public const string StockReserved = "inventory.stock-reserved";
    public const string StockReservationFailed = "inventory.stock-reservation-failed";
    public const string StockReleased = "inventory.stock-released";

    public const string PaymentProcessed = "payment.processed";
    public const string PaymentFailed = "payment.failed";
}

/// <summary>A durable queue, its bindings and (implicitly) its dead-letter queue.</summary>
public sealed record QueueDefinition(string Name, string OwnerService, IReadOnlyList<string> RoutingKeys);

/// <summary>
/// Single source of truth for the broker topology:
/// <code>
///  [onlinestore.events] (topic, durable)
///     order.placed ...................................> inventory.order-placed   (InventoryService)
///     payment.processed | payment.failed | order.cancelled > inventory.order-outcomes (InventoryService)
///     inventory.stock-reserved | ...-failed | ...-released > order.inventory-replies  (OrderService)
///
///  [onlinestore.dlx] (direct, durable)
///     &lt;queue name&gt; ...................................> &lt;queue name&gt;.dlq
/// </code>
/// Every service declares the whole (idempotent) topology on startup, so start-up order never
/// causes unroutable messages. Messages are published with <c>mandatory: true</c> as a safety net.
/// </summary>
public static class MessagingTopology
{
    public const string InventoryOrderPlacedQueue = "inventory.order-placed";
    public const string InventoryOrderOutcomesQueue = "inventory.order-outcomes";
    public const string OrderInventoryRepliesQueue = "order.inventory-replies";

    public static IReadOnlyList<QueueDefinition> Queues { get; } =
    [
        new(InventoryOrderPlacedQueue, "InventoryService", [RoutingKeys.OrderPlaced]),
        new(InventoryOrderOutcomesQueue, "InventoryService",
            [RoutingKeys.PaymentProcessed, RoutingKeys.PaymentFailed, RoutingKeys.OrderCancelled]),
        new(OrderInventoryRepliesQueue, "OrderService",
            [RoutingKeys.StockReserved, RoutingKeys.StockReservationFailed, RoutingKeys.StockReleased])
    ];

    public static string DeadLetterQueueName(string queueName, RabbitMqOptions options) =>
        queueName + options.DeadLetterQueueSuffix;
}
