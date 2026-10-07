// File: src/OnlineStoreSystem.InventoryService/Models/StockReservation.cs
// SCAFFOLDED (database-first) from InventoryDb.dbo.StockReservations - see the command in Data/InventoryDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.InventoryService.Models;

public partial class StockReservation
{
    public long StockReservationId { get; set; }

    public Guid OrderId { get; set; }

    public int InventoryItemId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public string Status { get; set; } = null!;

    public string? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public virtual InventoryItem InventoryItem { get; set; } = null!;
}
