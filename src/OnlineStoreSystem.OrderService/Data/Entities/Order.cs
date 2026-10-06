// File: src/OnlineStoreSystem.OrderService/Data/Entities/Order.cs
// SCAFFOLDED (database-first) from OrderDb.dbo.Orders - see the command in Data/OrderDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.OrderService.Data.Entities;

public partial class Order
{
    public Guid OrderId { get; set; }

    public string OrderNumber { get; set; } = null!;

    public string CustomerId { get; set; } = null!;

    public string CustomerEmail { get; set; } = null!;

    public string Status { get; set; } = null!;

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = null!;

    public string? FailureReason { get; set; }

    public Guid? PaymentId { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual SagaState? SagaState { get; set; }
}
