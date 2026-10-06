// File: src/OnlineStoreSystem.Shared/Messaging/OutgoingMessage.cs
namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// A fully serialized message ready to be published. Used directly by the transactional outbox
/// relay, which stores already-serialized payloads.
/// </summary>
public sealed record OutgoingMessage(
    Guid MessageId,
    string MessageType,
    string RoutingKey,
    ReadOnlyMemory<byte> Body,
    string? CorrelationId,
    DateTimeOffset OccurredAtUtc);
