// File: src/OnlineStoreSystem.OrderService/Outbox/OutboxMessageFactory.cs
using System.Text.Json;
using OnlineStoreSystem.OrderService.Models;
using OnlineStoreSystem.Shared.Events;
using OnlineStoreSystem.Shared.Messaging;

namespace OnlineStoreSystem.OrderService.Outbox;

public static class OutboxMessageFactory
{
    /// <summary>
    /// Serializes an integration event into an <see cref="OutboxMessage"/> row. The row is added to the same
    /// DbContext as the saga/order changes, so state change and event are committed atomically.
    /// </summary>
    public static OutboxMessage Create(IntegrationEvent integrationEvent, string routingKey, DateTime createdAtUtc) => new()
    {
        OutboxMessageId = integrationEvent.EventId,
        MessageType = integrationEvent.MessageType,
        RoutingKey = routingKey,
        Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), MessagingJson.Options),
        CorrelationId = integrationEvent.CorrelationId,
        CreatedAtUtc = createdAtUtc,
        Attempts = 0
    };
}
