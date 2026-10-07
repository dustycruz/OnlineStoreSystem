// File: src/OnlineStoreSystem.PaymentService/Models/PaymentRefund.cs
// SCAFFOLDED (database-first) from PaymentDb.dbo.PaymentRefunds - see the command in Data/PaymentDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.PaymentService.Models;

public partial class PaymentRefund
{
    public Guid RefundId { get; set; }

    public Guid PaymentId { get; set; }

    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public virtual PaymentTransaction Payment { get; set; } = null!;
}
