// File: src/OnlineStoreSystem.InventoryService/Data/InventoryDbContext.cs
// SCAFFOLDED (database-first) - reverse-engineered from InventoryDb with:
//   dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=InventoryDb;Trusted_Connection=True;TrustServerCertificate=True"
//     Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.InventoryService
//     --context InventoryDbContext --context-dir Data --output-dir Models
//     --namespace OnlineStoreSystem.InventoryService.Models
//     --context-namespace OnlineStoreSystem.InventoryService.Data --no-onconfiguring --force
//   Do not edit by hand: customisations live in InventoryDbContext.Customizations.cs.
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.InventoryService.Models;

namespace OnlineStoreSystem.InventoryService.Data;

public partial class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<InventoryItem> InventoryItems { get; set; }

    public virtual DbSet<ProcessedMessage> ProcessedMessages { get; set; }

    public virtual DbSet<StockReservation> StockReservations { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryItem>(entity =>
        {
            entity.HasIndex(e => e.ProductId, "UX_InventoryItems_ProductId").IsUnique();

            entity.HasIndex(e => e.Sku, "UX_InventoryItems_Sku").IsUnique();

            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_InventoryItems_CreatedAtUtc");
            entity.Property(e => e.QuantityAvailable).HasComputedColumnSql("([QuantityOnHand]-[QuantityReserved])", true);
            entity.Property(e => e.ReorderThreshold).HasDefaultValue(5, "DF_InventoryItems_ReorderThreshold");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.Sku).HasMaxLength(64);
            entity.Property(e => e.UpdatedAtUtc).HasPrecision(3);
        });

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.HasKey(e => new { e.MessageId, e.Consumer });

            entity.HasIndex(e => e.ProcessedAtUtc, "IX_ProcessedMessages_ProcessedAtUtc");

            entity.Property(e => e.Consumer).HasMaxLength(200);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.MessageType).HasMaxLength(200);
            entity.Property(e => e.ProcessedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_ProcessedMessages_ProcessedAtUtc");
        });

        modelBuilder.Entity<StockReservation>(entity =>
        {
            entity.HasIndex(e => e.InventoryItemId, "IX_StockReservations_InventoryItemId");

            entity.HasIndex(e => e.Status, "IX_StockReservations_Status");

            entity.HasIndex(e => new { e.OrderId, e.InventoryItemId }, "UX_StockReservations_OrderId_InventoryItemId").IsUnique();

            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_StockReservations_CreatedAtUtc");
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.UpdatedAtUtc).HasPrecision(3);

            entity.HasOne(d => d.InventoryItem).WithMany(p => p.StockReservations)
                .HasForeignKey(d => d.InventoryItemId)
                .HasConstraintName("FK_StockReservations_InventoryItems");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
