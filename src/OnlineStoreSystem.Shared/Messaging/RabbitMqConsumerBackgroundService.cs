// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqConsumerBackgroundService.cs
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Events;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Base class for RabbitMQ consumers hosted as <see cref="BackgroundService"/>s.
/// <para>Delivery semantics (at-least-once, explicit acknowledgements):</para>
/// <list type="bullet">
///   <item><c>autoAck: false</c> + <c>BasicQos(prefetch)</c>: the broker keeps every message until we ACK it.</item>
///   <item>Handler succeeded → <c>BasicAck</c>.</item>
///   <item><see cref="PoisonMessageException"/> (bad JSON, unknown type, invalid payload) →
///         <c>BasicNack(requeue: false)</c> → routed by the queue's <c>x-dead-letter-exchange</c> to <c>&lt;queue&gt;.dlq</c>.</item>
///   <item>Any other failure on the FIRST delivery → <c>BasicNack(requeue: true)</c> after a short delay (one retry).</item>
///   <item>Failure again on a redelivered message → <c>BasicNack(requeue: false)</c> → dead-letter queue.</item>
/// </list>
/// Handlers must be idempotent because redelivery can happen at any time (see InventoryDb.ProcessedMessages).
/// The correlation id of the message is restored into <see cref="ICorrelationIdAccessor"/> and a logging
/// scope, so logs and outgoing HTTP calls/events made while handling the message keep the same trace.
/// </summary>
public abstract class RabbitMqConsumerBackgroundService : BackgroundService
{
    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICorrelationIdAccessor _correlationIdAccessor;
    private readonly RabbitMqOptions _options;
    private IChannel? _channel;
    private CancellationToken _stoppingToken;

    protected RabbitMqConsumerBackgroundService(
        IRabbitMqConnectionProvider connectionProvider,
        IServiceScopeFactory scopeFactory,
        ICorrelationIdAccessor correlationIdAccessor,
        IOptions<RabbitMqOptions> options,
        ILogger logger)
    {
        _connectionProvider = connectionProvider;
        _scopeFactory = scopeFactory;
        _correlationIdAccessor = correlationIdAccessor;
        _options = options.Value;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>Queue to consume from (declared in <see cref="MessagingTopology"/>).</summary>
    protected abstract string QueueName { get; }

    /// <summary>Name stored in the idempotency ledger; defaults to the class name.</summary>
    protected virtual string ConsumerName => GetType().Name;

    /// <summary>Map of AMQP <c>type</c> property → CLR type this consumer understands.</summary>
    protected abstract IReadOnlyDictionary<string, Type> SupportedMessageTypes { get; }

    /// <summary>
    /// Processes one message inside a fresh DI scope. Return normally to ACK; throw
    /// <see cref="PoisonMessageException"/> to dead-letter; throw anything else to retry once.
    /// </summary>
    protected abstract Task HandleAsync(
        IntegrationEvent message,
        MessageContext context,
        IServiceProvider services,
        CancellationToken cancellationToken);

    private string DeadLetterQueue => MessagingTopology.DeadLetterQueueName(QueueName, _options);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        // Do not block host start-up while the broker is (still) unreachable.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await _connectionProvider.GetConnectionAsync(stoppingToken);
                await StartConsumingAsync(connection, stoppingToken);
                await WatchChannelAsync(connection, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Consumer} cannot consume from {Queue}; retrying in {DelaySeconds}s",
                    ConsumerName, QueueName, _options.ConnectRetryDelaySeconds);
            }

            await CloseChannelAsync();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.ConnectRetryDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task StartConsumingAsync(IConnection connection, CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken);

        // Fair dispatch: at most PrefetchCount un-ACKed messages in flight on this channel.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnReceivedAsync;

        var consumerTag = await channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false, // <-- manual acknowledgements
            consumer: consumer,
            cancellationToken: cancellationToken);

        _channel = channel;

        Logger.LogInformation(
            "{Consumer} consuming {Queue} (tag {ConsumerTag}, prefetch {Prefetch}, manual ACK, dead-letter queue {DeadLetterQueue})",
            ConsumerName, QueueName, consumerTag, _options.PrefetchCount, DeadLetterQueue);
    }

    /// <summary>
    /// Connection-level failures are healed by automatic recovery. A channel closed by the broker
    /// while the connection is still open (channel-level error) is not, so we rebuild it ourselves.
    /// </summary>
    private async Task WatchChannelAsync(IConnection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            if (_channel is { IsClosed: true } && connection.IsOpen)
            {
                Logger.LogWarning("{Consumer} channel was closed by the broker ({Reason}); re-creating it",
                    ConsumerName, _channel.CloseReason?.ReplyText);
                return;
            }
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;
        var properties = delivery.BasicProperties;
        var messageType = properties.Type ?? "(none)";
        var correlationId = CorrelationIdGenerator.Sanitize(
            properties.CorrelationId ?? ReadHeader(properties.Headers, CorrelationIdConstants.HeaderName));

        _correlationIdAccessor.CorrelationId = correlationId;

        using var logScope = Logger.BeginScope(new Dictionary<string, object>
        {
            [CorrelationIdConstants.LogPropertyName] = correlationId,
            ["MessageId"] = properties.MessageId ?? string.Empty,
            ["MessageType"] = messageType,
            ["Consumer"] = ConsumerName
        });

        DeliveryOutcome outcome;
        try
        {
            var (message, messageId) = Deserialize(properties, delivery.Body, messageType);
            var context = new MessageContext(messageId, messageType, correlationId, delivery.RoutingKey,
                delivery.Redelivered, delivery.DeliveryTag, ConsumerName);

            Logger.LogInformation(
                "Received {MessageType} {MessageId} from {Queue} (routing key {RoutingKey}, redelivered: {Redelivered})",
                messageType, messageId, QueueName, delivery.RoutingKey, delivery.Redelivered);

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                await HandleAsync(message, context, scope.ServiceProvider, _stoppingToken);
            }

            outcome = DeliveryOutcome.Ack;
        }
        catch (PoisonMessageException ex)
        {
            Logger.LogError(ex,
                "Poison message {MessageType} on {Queue}: {Reason} -> BasicNack(requeue: false) => dead-letter queue {DeadLetterQueue}",
                messageType, QueueName, ex.Message, DeadLetterQueue);
            outcome = DeliveryOutcome.DeadLetter;
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            Logger.LogInformation("{Consumer} stopping; returning {MessageType} to the queue", ConsumerName, messageType);
            outcome = DeliveryOutcome.Requeue;
        }
        catch (Exception ex) when (!delivery.Redelivered)
        {
            Logger.LogWarning(ex,
                "Processing {MessageType} failed on first delivery -> BasicNack(requeue: true) in {DelayMs} ms for one retry",
                messageType, _options.RequeueDelayMilliseconds);
            await DelayIgnoringShutdownAsync(_options.RequeueDelayMilliseconds);
            outcome = DeliveryOutcome.Requeue;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Processing {MessageType} failed again after redelivery -> BasicNack(requeue: false) => dead-letter queue {DeadLetterQueue}",
                messageType, DeadLetterQueue);
            outcome = DeliveryOutcome.DeadLetter;
        }

        try
        {
            switch (outcome)
            {
                case DeliveryOutcome.Ack:
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
                    Logger.LogInformation("BasicAck {MessageType} (delivery tag {DeliveryTag})", messageType, delivery.DeliveryTag);
                    break;
                case DeliveryOutcome.Requeue:
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
                    break;
                case DeliveryOutcome.DeadLetter:
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            // The channel died between processing and acknowledging: the broker will redeliver the
            // message (Redelivered = true) and the idempotent handler will recognise it.
            Logger.LogWarning(ex, "Could not send {Outcome} for delivery tag {DeliveryTag}; message will be redelivered",
                outcome, delivery.DeliveryTag);
        }
        finally
        {
            _correlationIdAccessor.CorrelationId = null;
        }
    }

    private (IntegrationEvent Message, Guid MessageId) Deserialize(
        IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, string messageType)
    {
        if (!SupportedMessageTypes.TryGetValue(messageType, out var clrType))
        {
            throw new PoisonMessageException($"Unsupported message type '{messageType}' on queue '{QueueName}'.");
        }

        IntegrationEvent? message;
        try
        {
            message = JsonSerializer.Deserialize(body.Span, clrType, MessagingJson.Options) as IntegrationEvent;
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"Body is not a valid {messageType} JSON document: {ex.Message}", ex);
        }

        if (message is null)
        {
            throw new PoisonMessageException($"Body of {messageType} deserialized to null.");
        }

        var messageId = Guid.TryParse(properties.MessageId, out var parsed) ? parsed : message.EventId;
        if (messageId == Guid.Empty)
        {
            throw new PoisonMessageException("Message has neither an AMQP message-id nor an EventId.");
        }

        return (message, messageId);
    }

    private static string? ReadHeader(IDictionary<string, object?>? headers, string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => value.ToString()
        };
    }

    private async Task DelayIgnoringShutdownAsync(int milliseconds)
    {
        try
        {
            await Task.Delay(milliseconds, _stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: requeue immediately.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await CloseChannelAsync();
    }

    private async Task CloseChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is null)
        {
            return;
        }

        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Ignoring error while closing consumer channel");
        }

        await channel.DisposeAsync();
    }

    private enum DeliveryOutcome
    {
        Ack,
        Requeue,
        DeadLetter
    }
}
