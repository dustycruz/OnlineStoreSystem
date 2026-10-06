// File: src/OnlineStoreSystem.OrderService/Application/OrderLines.cs
using OnlineStoreSystem.OrderService.Data.Entities;

namespace OnlineStoreSystem.OrderService.Application;

public static class OrderLines
{
    /// <summary>
    /// Synchronises <see cref="Order.OrderItems"/> with freshly priced lines IN PLACE (update / insert / delete),
    /// which keeps the (OrderId, ProductId) unique index happy, and recomputes the order total.
    /// </summary>
    public static void Apply(Order order, IReadOnlyList<PricedLine> lines, string currency)
    {
        var wanted = lines.ToDictionary(line => line.ProductId);

        foreach (var existing in order.OrderItems.Where(item => !wanted.ContainsKey(item.ProductId)).ToList())
        {
            order.OrderItems.Remove(existing); // orphan => deleted on SaveChanges (required relationship)
        }

        foreach (var line in lines)
        {
            var item = order.OrderItems.SingleOrDefault(i => i.ProductId == line.ProductId);
            if (item is null)
            {
                item = new OrderItem { ProductId = line.ProductId };
                order.OrderItems.Add(item);
            }

            item.Sku = line.Sku;
            item.ProductName = line.ProductName;
            item.UnitPrice = line.UnitPrice;
            item.Quantity = line.Quantity;
            item.LineTotal = line.UnitPrice * line.Quantity; // mirrors the computed column until re-read
        }

        order.TotalAmount = lines.Sum(line => line.UnitPrice * line.Quantity);
        order.Currency = currency;
    }
}
