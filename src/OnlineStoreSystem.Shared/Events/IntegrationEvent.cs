// File: src/OnlineStoreSystem.Shared/Events/IntegrationEvent.cs
using OnlineStoreSystem.Shared.Identifiers;

namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Base contract for every message exchanged over RabbitMQ.
/// <see cref="EventId"/> is also the AMQP <c>message-id</c> and the key used by idempotent
/// consumers (InventoryDb.ProcessedMessages) to detect redeliveries.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = SequentialGuid.NewGuid();

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? CorrelationId { get; init; }

    /// <summary>Contract version; bump on breaking schema changes.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The logical message type written to the AMQP <c>type</c> property.</summary>
    public string MessageType => GetType().Name;
}
