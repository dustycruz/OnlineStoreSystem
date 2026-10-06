// File: src/OnlineStoreSystem.OrderService/Data/Entities/SagaState.cs
// SCAFFOLDED (database-first) from OrderDb.dbo.SagaStates - see the command in Data/OrderDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.OrderService.Data.Entities;

public partial class SagaState
{
    public Guid SagaId { get; set; }

    public Guid OrderId { get; set; }

    public string Status { get; set; } = null!;

    public string CurrentStep { get; set; } = null!;

    public bool StockReserved { get; set; }

    public bool PaymentProcessed { get; set; }

    public Guid? PaymentId { get; set; }

    public string? ProtectedPaymentDetails { get; set; }

    public string? PaymentCardLast4 { get; set; }

    public int RetryCount { get; set; }

    public string? LastError { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual Order Order { get; set; } = null!;

    public virtual ICollection<SagaStepLog> SagaStepLogs { get; set; } = new List<SagaStepLog>();
}
