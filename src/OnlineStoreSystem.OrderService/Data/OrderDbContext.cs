// File: src/OnlineStoreSystem.OrderService/Data/OrderDbContext.cs
// SCAFFOLDED (database-first) - reverse-engineered from OrderDb with:
//   dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=OrderDb;Trusted_Connection=True;TrustServerCertificate=True"
//     Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.OrderService
//     --context OrderDbContext --context-dir Data --output-dir Models
//     --namespace OnlineStoreSystem.OrderService.Models
//     --context-namespace OnlineStoreSystem.OrderService.Data --no-onconfiguring --force
//   Do not edit by hand: re-run the command after changing sql/04_OrderDb.sql.
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.OrderService.Models;

namespace OnlineStoreSystem.OrderService.Data;

public partial class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Order> Orders { get; set; }

    public virtual DbSet<OrderItem> OrderItems { get; set; }

    public virtual DbSet<OutboxMessage> OutboxMessages { get; set; }

    public virtual DbSet<SagaState> SagaStates { get; set; }

    public virtual DbSet<SagaStepLog> SagaStepLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasIndex(e => e.CustomerId, "IX_Orders_CustomerId");

            entity.HasIndex(e => new { e.Status, e.CreatedAtUtc }, "IX_Orders_Status_CreatedAtUtc");

            entity.HasIndex(e => e.OrderNumber, "UX_Orders_OrderNumber").IsUnique();

            entity.Property(e => e.OrderId).HasDefaultValueSql("(newsequentialid())", "DF_Orders_OrderId");
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_Orders_CreatedAtUtc");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasDefaultValue("USD", "DF_Orders_Currency");
            entity.Property(e => e.CustomerEmail).HasMaxLength(256);
            entity.Property(e => e.CustomerId).HasMaxLength(100);
            entity.Property(e => e.FailureReason).HasMaxLength(1000);
            entity.Property(e => e.OrderNumber).HasMaxLength(32);
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.Status).HasMaxLength(30);
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.UpdatedAtUtc).HasPrecision(3);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasIndex(e => new { e.OrderId, e.ProductId }, "UX_OrderItems_OrderId_ProductId").IsUnique();

            entity.Property(e => e.LineTotal)
                .HasComputedColumnSql("(CONVERT([decimal](18,2),[UnitPrice]*[Quantity]))", true)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ProductName).HasMaxLength(200);
            entity.Property(e => e.Sku).HasMaxLength(64);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 2)");

            entity.HasOne(d => d.Order).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.OrderId)
                .HasConstraintName("FK_OrderItems_Orders");
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasIndex(e => e.CreatedAtUtc, "IX_OutboxMessages_Pending").HasFilter("([ProcessedAtUtc] IS NULL)");

            entity.Property(e => e.OutboxMessageId).ValueGeneratedNever();
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_OutboxMessages_CreatedAtUtc");
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.MessageType).HasMaxLength(200);
            entity.Property(e => e.ProcessedAtUtc).HasPrecision(3);
            entity.Property(e => e.RoutingKey).HasMaxLength(200);
        });

        modelBuilder.Entity<SagaState>(entity =>
        {
            entity.HasKey(e => e.SagaId);

            entity.HasIndex(e => new { e.Status, e.CurrentStep, e.UpdatedAtUtc }, "IX_SagaStates_Status_CurrentStep_UpdatedAtUtc");

            entity.HasIndex(e => e.OrderId, "UX_SagaStates_OrderId").IsUnique();

            entity.Property(e => e.SagaId).HasDefaultValueSql("(newsequentialid())", "DF_SagaStates_SagaId");
            entity.Property(e => e.CompletedAtUtc).HasPrecision(3);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.CurrentStep).HasMaxLength(50);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.PaymentCardLast4)
                .HasMaxLength(4)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.ProtectedPaymentDetails).HasMaxLength(2000);
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.StartedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_SagaStates_StartedAtUtc");
            entity.Property(e => e.Status).HasMaxLength(30);
            entity.Property(e => e.UpdatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_SagaStates_UpdatedAtUtc");

            entity.HasOne(d => d.Order).WithOne(p => p.SagaState)
                .HasForeignKey<SagaState>(d => d.OrderId)
                .HasConstraintName("FK_SagaStates_Orders");
        });

        modelBuilder.Entity<SagaStepLog>(entity =>
        {
            entity.HasIndex(e => new { e.SagaId, e.OccurredAtUtc }, "IX_SagaStepLogs_SagaId_OccurredAtUtc");

            entity.Property(e => e.Detail).HasMaxLength(2000);
            entity.Property(e => e.OccurredAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())", "DF_SagaStepLogs_OccurredAtUtc");
            entity.Property(e => e.Outcome).HasMaxLength(20);
            entity.Property(e => e.StepName).HasMaxLength(50);

            entity.HasOne(d => d.Saga).WithMany(p => p.SagaStepLogs)
                .HasForeignKey(d => d.SagaId)
                .HasConstraintName("FK_SagaStepLogs_SagaStates");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
