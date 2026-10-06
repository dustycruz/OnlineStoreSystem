// File: src/OnlineStoreSystem.CatalogService/Data/Entities/Product.cs
// SCAFFOLDED (database-first) from CatalogDb.dbo.Products - see the command in Data/CatalogDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.CatalogService.Data.Entities;

public partial class Product
{
    public int ProductId { get; set; }

    public string Sku { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public string Category { get; set; } = null!;

    public decimal Price { get; set; }

    public string Currency { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
