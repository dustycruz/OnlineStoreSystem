// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqPublisher.cs
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Events;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// RabbitMQ publisher with <b>publisher confirms</b>.
/// <para>
/// The dedicated channel is opened with <c>publisherConfirmationsEnabled</c> and
/// <c>publisherConfirmationTrackingEnabled</c>; therefore <c>BasicPublishAsync</c> only returns after the
/// broker sent <c>basic.ack</c> for the message. A <c>basic.nack</c>, or a <c>basic.return</c> of an
/// unroutable <c>mandatory</c> message, surfaces as <see cref="PublishException"/> so the caller
/// (outbox relay / consumer) can retry instead of silently losing the event.
/// </para>
/// Messages are persistent (delivery-mode 2) and carry <c>message-id</c> (= EventId),
/// <c>correlation-id</c>, <c>type</c> and an <c>X-Correlation-Id</c> header.
/// </summary>
public sealed class RabbitMqPublisher(
    IRabbitMqConnectionProvider connectionProvider,
    IOptions<RabbitMqOptions> options,
    ICorrelationIdAccessor correlationIdAccessor,
    ILogger<RabbitMqPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    // IChannel is not meant for concurrent publishing; serialize access.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public Task PublishAsync<TEvent>(TEvent integrationEvent, string routingKey, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);

        var correlationId = integrationEvent.CorrelationId ?? correlationIdAccessor.CorrelationId;
        var body = JsonSerializer.SerializeToUtf8Bytes(integrationEvent, integrationEvent.GetType(), MessagingJson.Options);

        var message = new OutgoingMessage(
            integrationEvent.EventId,
            integrationEvent.MessageType,
            routingKey,
            body,
            correlationId,
            integrationEvent.OccurredAtUtc);

        return PublishAsync(message, cancellationToken);
    }

    public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetOrCreateChannelAsync(settings, cancellationToken);

            var properties = new BasicProperties
            {
                MessageId = message.MessageId.ToString(),
                CorrelationId = message.CorrelationId,
                Type = message.MessageType,
                ContentType = "application/json",
                ContentEncoding = "utf-8",
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(message.OccurredAtUtc.ToUnixTimeSeconds()),
                AppId = settings.ClientProvidedName,
                Headers = new Dictionary<string, object?>
                {
                    [CorrelationIdConstants.HeaderName] = message.CorrelationId,
                    ["x-occurred-at"] = message.OccurredAtUtc.ToString("O")
                }
            };

            using var confirmTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            confirmTimeout.CancelAfter(TimeSpan.FromSeconds(settings.PublishConfirmTimeoutSeconds));

            // Awaits the publisher confirm (basic.ack) because confirmation tracking is enabled.
            await channel.BasicPublishAsync(
                exchange: settings.ExchangeName,
                routingKey: message.RoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: message.Body,
                cancellationToken: confirmTimeout.Token);

            logger.LogInformation(
                "Published {MessageType} {MessageId} to {Exchange}/{RoutingKey} (broker confirmed)",
                message.MessageType, message.MessageId, settings.ExchangeName, message.RoutingKey);
        }
        catch (PublishException ex)
        {
            logger.LogError(ex,
                "Broker rejected {MessageType} {MessageId} (routing key {RoutingKey}; returned: {IsReturn})",
                message.MessageType, message.MessageId, message.RoutingKey, ex.IsReturn);
            await ResetChannelAsync();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Timed out after {Timeout}s waiting for publisher confirm of {MessageType} {MessageId}",
                settings.PublishConfirmTimeoutSeconds, message.MessageType, message.MessageId);
            await ResetChannelAsync();
            throw new TimeoutException(
                $"No publisher confirm received for message {message.MessageId} within {settings.PublishConfirmTimeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is AlreadyClosedException or BrokerUnreachableException or OperationInterruptedException)
        {
            logger.LogWarning(ex, "RabbitMQ unavailable while publishing {MessageType} {MessageId}",
                message.MessageType, message.MessageId);
            await ResetChannelAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> GetOrCreateChannelAsync(RabbitMqOptions settings, CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await ResetChannelAsync();

        var connection = await connectionProvider.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await RabbitMqConfiguration.DeclareTopologyAsync(channel, settings, cancellationToken);

        logger.LogInformation("Opened RabbitMQ publisher channel #{ChannelNumber} with publisher confirms enabled",
            channel.ChannelNumber);

        _channel = channel;
        return channel;
    }

    private async Task ResetChannelAsync()
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
            logger.LogDebug(ex, "Ignoring error while closing publisher channel");
        }

        await channel.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ResetChannelAsync();
        _gate.Dispose();
    }
}
