// File: src/OnlineStoreSystem.PaymentService/Data/PaymentDbContext.cs
// SCAFFOLDED (database-first) - reverse-engineered from PaymentDb with:
//   dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=PaymentDb;Trusted_Connection=True;TrustServerCertificate=True"
//     Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.PaymentService
//     --context PaymentDbContext --context-dir Data --output-dir Data/Entities
//     --namespace OnlineStoreSystem.PaymentService.Data.Entities
//     --context-namespace OnlineStoreSystem.PaymentService.Data --no-onconfiguring --force
//   Do not edit by hand: re-run the command after changing sql/03_PaymentDb.sql.
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.PaymentService.Data.Entities;

namespace OnlineStoreSystem.PaymentService.Data;

public partial class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<PaymentRefund> PaymentRefunds { get; set; }

    public virtual DbSet<PaymentTransaction> PaymentTransactions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentRefund>(entity =>
        {
            entity.HasKey(e => e.RefundId);

            entity.HasIndex(e => e.PaymentId, "IX_PaymentRefunds_PaymentId");

            entity.Property(e => e.RefundId).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Reason).HasMaxLength(500);

            entity.HasOne(d => d.Payment).WithMany(p => p.PaymentRefunds)
                .HasForeignKey(d => d.PaymentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PaymentRefunds_PaymentTransactions");
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(e => e.PaymentId);

            entity.HasIndex(e => e.OrderId, "IX_PaymentTransactions_OrderId");

            entity.HasIndex(e => e.IdempotencyKey, "UX_PaymentTransactions_IdempotencyKey")
                .IsUnique()
                .HasFilter("([IdempotencyKey] IS NOT NULL)");

            entity.Property(e => e.PaymentId).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CardBrand).HasMaxLength(20);
            entity.Property(e => e.CardHolderName).HasMaxLength(200);
            entity.Property(e => e.CardLast4)
                .HasMaxLength(4)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.CreatedAtUtc)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.DeclineCode).HasMaxLength(50);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(100);
            entity.Property(e => e.RefundedAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.RequestHash)
                .HasMaxLength(64)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.UpdatedAtUtc).HasPrecision(3);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
