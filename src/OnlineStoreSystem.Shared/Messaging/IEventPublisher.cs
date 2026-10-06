// File: src/OnlineStoreSystem.Shared/Messaging/IEventPublisher.cs
using OnlineStoreSystem.Shared.Events;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Publishes integration events. Implementations must only complete successfully once the broker
/// has confirmed (persisted) the message.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, string routingKey, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent;

    Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken);
}
