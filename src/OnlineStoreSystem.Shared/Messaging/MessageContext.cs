// File: src/OnlineStoreSystem.Shared/Messaging/MessageContext.cs
namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>Delivery metadata handed to a consumer's handler.</summary>
public sealed record MessageContext(
    Guid MessageId,
    string MessageType,
    string CorrelationId,
    string RoutingKey,
    bool Redelivered,
    ulong DeliveryTag,
    string ConsumerName);
