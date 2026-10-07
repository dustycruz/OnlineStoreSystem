// File: src/OnlineStoreSystem.PaymentService/Models/PaymentTransaction.cs
// SCAFFOLDED (database-first) from PaymentDb.dbo.PaymentTransactions - see the command in Data/PaymentDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.PaymentService.Models;

public partial class PaymentTransaction
{
    public Guid PaymentId { get; set; }

    public Guid OrderId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string CardHolderName { get; set; } = null!;

    public string CardLast4 { get; set; } = null!;

    public string CardBrand { get; set; } = null!;

    public string? DeclineCode { get; set; }

    public string? FailureReason { get; set; }

    public decimal RefundedAmount { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? RequestHash { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ICollection<PaymentRefund> PaymentRefunds { get; set; } = new List<PaymentRefund>();
}
