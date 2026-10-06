// File: src/OnlineStoreSystem.Shared/Events/OrderCancelledEvent.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Emitted by the saga orchestrator when an order is cancelled for a reason other than a payment
/// decline (e.g. InventoryService did not answer before the saga timeout, or a reservation arrived
/// for an order that was already cancelled). InventoryService releases any reservation it holds.
/// Routing key: <c>order.cancelled</c>.
/// </summary>
public sealed record OrderCancelledEvent : IntegrationEvent
{
    public required Guid OrderId { get; init; }

    public required string Reason { get; init; }
}
