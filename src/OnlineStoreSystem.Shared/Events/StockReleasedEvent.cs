// File: src/OnlineStoreSystem.Shared/Events/StockReleasedEvent.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Reply from InventoryService confirming the "Release Stock" compensation ran
/// (triggered by <see cref="PaymentFailedEvent"/> or <see cref="OrderCancelledEvent"/>).
/// The orchestrator then marks the saga <c>Compensated</c>.
/// Routing key: <c>inventory.stock-released</c>.
/// </summary>
public sealed record StockReleasedEvent : IntegrationEvent
{
    public required Guid OrderId { get; init; }

    public required IReadOnlyList<ReleasedStockLine> Items { get; init; }

    /// <summary>Message type that triggered the release (PaymentFailedEvent / OrderCancelledEvent).</summary>
    public required string TriggeredBy { get; init; }

    public bool IsReplay { get; init; }
}
