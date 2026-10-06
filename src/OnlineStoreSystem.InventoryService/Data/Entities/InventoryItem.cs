// File: src/OnlineStoreSystem.InventoryService/Data/Entities/InventoryItem.cs
// SCAFFOLDED (database-first) from InventoryDb.dbo.InventoryItems - see the command in Data/InventoryDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.InventoryService.Data.Entities;

public partial class InventoryItem
{
    public int InventoryItemId { get; set; }

    public int ProductId { get; set; }

    public string Sku { get; set; } = null!;

    public int QuantityOnHand { get; set; }

    public int QuantityReserved { get; set; }

    public int QuantityAvailable { get; set; }

    public int ReorderThreshold { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ICollection<StockReservation> StockReservations { get; set; } = new List<StockReservation>();
}
