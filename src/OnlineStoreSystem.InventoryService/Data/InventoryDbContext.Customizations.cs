// File: src/OnlineStoreSystem.InventoryService/Data/InventoryDbContext.Customizations.cs
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.InventoryService.Data.Entities;

namespace OnlineStoreSystem.InventoryService.Data;

/// <summary>
/// Hand-written half of the scaffolded partial class: survives <c>dotnet ef dbcontext scaffold --force</c>.
/// </summary>
public partial class InventoryDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // DF_InventoryItems_ReorderThreshold = 5 makes EF treat the column as store-generated, so an
        // explicit value equal to the CLR default (0) would silently be replaced by 5 on INSERT.
        // The API always supplies the value, therefore EF must always send it.
        modelBuilder.Entity<InventoryItem>()
            .Property(e => e.ReorderThreshold)
            .ValueGeneratedNever();
    }
}
