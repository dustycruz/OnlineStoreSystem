// File: src/OnlineStoreSystem.Shared/Events/EventLineItems.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>An order line as carried by <see cref="OrderPlacedEvent"/>.</summary>
public sealed record OrderLineItem(int ProductId, string Sku, int Quantity, decimal UnitPrice);

/// <summary>A line that InventoryService reserved for an order.</summary>
public sealed record ReservedStockLine(int ProductId, int Quantity, int RemainingAvailable);

/// <summary>A line that could not be reserved (requested more than available).</summary>
public sealed record StockShortage(int ProductId, int Requested, int Available);

/// <summary>A line whose reservation was released (compensation).</summary>
public sealed record ReleasedStockLine(int ProductId, int Quantity);
