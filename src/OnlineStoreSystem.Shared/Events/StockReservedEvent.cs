// File: src/OnlineStoreSystem.Shared/Events/StockReservedEvent.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Reply from InventoryService: every line of the order was reserved.
/// The saga orchestrator continues with the "Process Payment" step.
/// Routing key: <c>inventory.stock-reserved</c>.
/// </summary>
public sealed record StockReservedEvent : IntegrationEvent
{
    public required Guid OrderId { get; init; }

    public required IReadOnlyList<ReservedStockLine> Items { get; init; }

    /// <summary>True when this reply was re-sent for a redelivered (already processed) message.</summary>
    public bool IsReplay { get; init; }
}
