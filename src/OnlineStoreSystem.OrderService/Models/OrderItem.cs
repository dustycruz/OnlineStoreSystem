// File: src/OnlineStoreSystem.OrderService/Models/OrderItem.cs
// SCAFFOLDED (database-first) from OrderDb.dbo.OrderItems - see the command in Data/OrderDbContext.cs.
using System;
using System.Collections.Generic;

namespace OnlineStoreSystem.OrderService.Models;

public partial class OrderItem
{
    public int OrderItemId { get; set; }

    public Guid OrderId { get; set; }

    public int ProductId { get; set; }

    public string Sku { get; set; } = null!;

    public string ProductName { get; set; } = null!;

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public decimal LineTotal { get; set; }

    public virtual Order Order { get; set; } = null!;
}
