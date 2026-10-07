// File: src/OnlineStoreSystem.InventoryService/Models/ProcessedMessage.cs
// SCAFFOLDED (database-first) from InventoryDb.dbo.ProcessedMessages - see the command in Data/InventoryDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.InventoryService.Models;

public partial class ProcessedMessage
{
    public Guid MessageId { get; set; }

    public string Consumer { get; set; } = null!;

    public string MessageType { get; set; } = null!;

    public string? CorrelationId { get; set; }

    public DateTime ProcessedAtUtc { get; set; }
}
