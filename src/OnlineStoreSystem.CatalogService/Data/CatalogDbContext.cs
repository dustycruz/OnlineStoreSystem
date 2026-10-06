// File: src/OnlineStoreSystem.CatalogService/Data/CatalogDbContext.cs
// SCAFFOLDED (database-first) - reverse-engineered from CatalogDb with:
//   dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=CatalogDb;Trusted_Connection=True;TrustServerCertificate=True"
//     Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.CatalogService
//     --context CatalogDbContext --context-dir Data --output-dir Data/Entities
//     --namespace OnlineStoreSystem.CatalogService.Data.Entities
//     --context-namespace OnlineStoreSystem.CatalogService.Data --no-onconfiguring --force
//   Do not edit by hand: re-run the command after changing sql/01_CatalogDb.sql.
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.CatalogService.Data.Entities;

namespace OnlineStoreSystem.CatalogService.Data;

public partial class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Product> Products { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(e => new { e.Category, e.IsActive }, "IX_Products_Category_IsActive");

            entity.HasIndex(e => e.Name, "IX_Products_Name");

            entity.HasIndex(e => e.Sku, "UX_Products_Sku").IsUnique();

            entity.Property(e => e.Category)
                .HasMaxLength(100)
                .HasDefaultValue("General");
            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("USD")
                .IsFixedLength();
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Price).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.Sku).HasMaxLength(64);
            entity.Property(e => e.UpdatedAtUtc).HasPrecision(3);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
