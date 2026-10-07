// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqConfiguration.cs
using System.ComponentModel.DataAnnotations;
using RabbitMQ.Client;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Bound from the <c>RabbitMq</c> configuration section of every service.
/// Either set <see cref="ConnectionString"/> (e.g. a CloudAMQP <c>amqps://</c> URL) or the
/// discrete host settings for a locally installed broker. No Docker is required.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>
    /// Full AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672/</c> or
    /// <c>amqps://user:pass@hostname.rmq.cloudamqp.com/vhost</c>. Takes precedence when set.
    /// </summary>
    public string? ConnectionString { get; set; }

    [Required]
    public string HostName { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    [Required]
    public string UserName { get; set; } = "guest";

    [Required]
    public string Password { get; set; } = "guest";

    [Required]
    public string VirtualHost { get; set; } = "/";

    /// <summary>Durable topic exchange all integration events are published to.</summary>
    [Required]
    public string ExchangeName { get; set; } = "onlinestore.events";

    /// <summary>Dead-letter exchange (direct). Poison messages are routed here on BasicNack(requeue: false).</summary>
    [Required]
    public string DeadLetterExchangeName { get; set; } = "onlinestore.dlx";

    /// <summary>Suffix appended to a queue name to build its dead-letter queue name.</summary>
    [Required]
    public string DeadLetterQueueSuffix { get; set; } = ".dlq";

    /// <summary>Maximum un-acknowledged deliveries per consumer channel (BasicQos).</summary>
    [Range(1, 1000)]
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>How long a publish waits for the broker's publisher confirm.</summary>
    [Range(1, 300)]
    public int PublishConfirmTimeoutSeconds { get; set; } = 10;

    /// <summary>Delay between attempts when the broker is unreachable at startup.</summary>
    [Range(1, 300)]
    public int ConnectRetryDelaySeconds { get; set; } = 5;

    /// <summary>Delay before a failed (non-poison) delivery is NACKed with requeue: true.</summary>
    [Range(0, 60_000)]
    public int RequeueDelayMilliseconds { get; set; } = 1000;

    /// <summary>Shown in the RabbitMQ management UI (Connections tab). Defaults to the service name.</summary>
    public string? ClientProvidedName { get; set; }

    /// <summary>Human-readable endpoint WITHOUT credentials, for logs.</summary>
    public string DescribeEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString) && Uri.TryCreate(ConnectionString, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}:{(uri.IsDefaultPort ? (uri.Scheme == "amqps" ? 5671 : 5672) : uri.Port)}{uri.AbsolutePath}";
        }

        return $"amqp://{HostName}:{Port}{(VirtualHost.StartsWith('/') ? VirtualHost : "/" + VirtualHost)}";
    }
}

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
/// Single source of truth for the broker topology, and the code that declares it:
/// <code>
///  [onlinestore.events] (topic, durable)
///     order.placed ...................................> inventory.order-placed   (InventoryService)
///     payment.processed | payment.failed | order.cancelled > inventory.order-outcomes (InventoryService)
///     inventory.stock-reserved | ...-failed | ...-released > order.inventory-replies  (OrderService)
///
///  [onlinestore.dlx] (direct, durable)
///     &lt;queue name&gt; ...................................> &lt;queue name&gt;.dlq
/// </code>
/// Every service declares the whole topology on startup, so start-up order never causes unroutable messages.
/// All declarations are idempotent (re-declaring with identical arguments is a no-op on the broker).
/// Messages are published with <c>mandatory: true</c> as a safety net.
/// </summary>
public static class RabbitMqConfiguration
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

    /// <summary>Declares both exchanges, every queue with its dead-letter queue, and all bindings.</summary>
    public static async Task DeclareTopologyAsync(IChannel channel, RabbitMqOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);

        // Main topic exchange for integration events.
        await channel.ExchangeDeclareAsync(
            exchange: options.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        // Dead-letter exchange: messages NACKed with requeue:false are re-routed here with
        // routing key = original queue name (see x-dead-letter-routing-key below).
        await channel.ExchangeDeclareAsync(
            exchange: options.DeadLetterExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        foreach (var queue in Queues)
        {
            await DeclareQueueWithDeadLetteringAsync(channel, options, queue, cancellationToken);
        }
    }

    private static async Task DeclareQueueWithDeadLetteringAsync(
        IChannel channel, RabbitMqOptions options, QueueDefinition queue, CancellationToken cancellationToken)
    {
        var deadLetterQueue = DeadLetterQueueName(queue.Name, options);

        await channel.QueueDeclareAsync(
            queue: deadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: deadLetterQueue,
            exchange: options.DeadLetterExchangeName,
            routingKey: queue.Name,
            arguments: null,
            cancellationToken: cancellationToken);

        var arguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = options.DeadLetterExchangeName,
            ["x-dead-letter-routing-key"] = queue.Name
        };

        await channel.QueueDeclareAsync(
            queue: queue.Name,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments,
            cancellationToken: cancellationToken);

        foreach (var routingKey in queue.RoutingKeys)
        {
            await channel.QueueBindAsync(
                queue: queue.Name,
                exchange: options.ExchangeName,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: cancellationToken);
        }
    }
}
