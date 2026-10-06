// File: src/OnlineStoreSystem.Shared/Events/PaymentProcessedEvent.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Emitted by the saga orchestrator (OrderService) once PaymentService captured the payment.
/// InventoryService commits the reservation (stock physically leaves on-hand quantity).
/// Routing key: <c>payment.processed</c>.
/// </summary>
public sealed record PaymentProcessedEvent : IntegrationEvent
{
    public required Guid OrderId { get; init; }

    public required Guid PaymentId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
