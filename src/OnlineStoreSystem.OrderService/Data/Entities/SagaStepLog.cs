// File: src/OnlineStoreSystem.OrderService/Data/Entities/SagaStepLog.cs
// SCAFFOLDED (database-first) from OrderDb.dbo.SagaStepLogs - see the command in Data/OrderDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.OrderService.Data.Entities;

public partial class SagaStepLog
{
    public long SagaStepLogId { get; set; }

    public Guid SagaId { get; set; }

    public string StepName { get; set; } = null!;

    public string Outcome { get; set; } = null!;

    public string? Detail { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public virtual SagaState Saga { get; set; } = null!;
}
