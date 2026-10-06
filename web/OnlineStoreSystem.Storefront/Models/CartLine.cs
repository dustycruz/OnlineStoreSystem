// File: web/OnlineStoreSystem.Storefront/Models/CartLine.cs
namespace OnlineStoreSystem.Storefront.Models;

/// <summary>A shopping-cart line kept in the ASP.NET Core session (display snapshot only;
/// OrderService re-prices every line from the catalog at checkout).</summary>
public sealed class CartLine
{
    public int ProductId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal UnitPrice { get; set; }

    public string Currency { get; set; } = "USD";

    public int Quantity { get; set; }

    public decimal LineTotal => UnitPrice * Quantity;
}
