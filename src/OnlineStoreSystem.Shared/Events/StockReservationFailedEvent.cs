// File: src/OnlineStoreSystem.Shared/Events/StockReservationFailedEvent.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Reply from InventoryService: the order could not be reserved (nothing was changed).
/// The saga cancels the order; no compensation is required.
/// Routing key: <c>inventory.stock-reservation-failed</c>.
/// </summary>
public sealed record StockReservationFailedEvent : IntegrationEvent
{
    public required Guid OrderId { get; init; }

    public required string Reason { get; init; }

    public required IReadOnlyList<StockShortage> Shortages { get; init; }

    public bool IsReplay { get; init; }
}
