// File: src/OnlineStoreSystem.OrderService/Data/Entities/OutboxMessage.cs
// SCAFFOLDED (database-first) from OrderDb.dbo.OutboxMessages - see the command in Data/OrderDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.OrderService.Data.Entities;

public partial class OutboxMessage
{
    public Guid OutboxMessageId { get; set; }

    public string MessageType { get; set; } = null!;

    public string RoutingKey { get; set; } = null!;

    public string Payload { get; set; } = null!;

    public string? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
