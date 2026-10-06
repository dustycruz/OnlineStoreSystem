// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqTopology.cs
using RabbitMQ.Client;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Declares exchanges, queues, dead-letter queues and bindings. All declarations are idempotent
/// (re-declaring with identical arguments is a no-op on the broker).
/// </summary>
public static class RabbitMqTopology
{
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken cancellationToken)
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

        foreach (var queue in MessagingTopology.Queues)
        {
            await DeclareQueueWithDeadLetteringAsync(channel, options, queue, cancellationToken);
        }
    }

    private static async Task DeclareQueueWithDeadLetteringAsync(
        IChannel channel, RabbitMqOptions options, QueueDefinition queue, CancellationToken cancellationToken)
    {
        var deadLetterQueue = MessagingTopology.DeadLetterQueueName(queue.Name, options);

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
