// File: web/OnlineStoreSystem.Storefront/Services/CartService.cs
using System.Text.Json;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Storefront.Models;

namespace OnlineStoreSystem.Storefront.Services;

/// <summary>Session-backed shopping cart.</summary>
public sealed class CartService(IHttpContextAccessor httpContextAccessor)
{
    private const string SessionKey = "OnlineStore.Cart";
    public const int MaxQuantityPerLine = 1000;

    private ISession Session =>
        httpContextAccessor.HttpContext?.Session ?? throw new InvalidOperationException("No active HTTP session.");

    public IReadOnlyList<CartLine> GetLines()
    {
        var json = Session.GetString(SessionKey);
        return string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<CartLine>>(json) ?? [];
    }

    public int ItemCount => GetLines().Sum(line => line.Quantity);

    public void Add(ProductDto product, int quantity)
    {
        var lines = GetLines().ToList();
        var line = lines.SingleOrDefault(l => l.ProductId == product.ProductId);
        if (line is null)
        {
            line = new CartLine
            {
                ProductId = product.ProductId,
                Sku = product.Sku,
                Name = product.Name,
                UnitPrice = product.Price,
                Currency = product.Currency
            };
            lines.Add(line);
        }

        line.Quantity = Math.Clamp(line.Quantity + quantity, 1, MaxQuantityPerLine);
        Save(lines);
    }

    public void SetQuantity(int productId, int quantity)
    {
        var lines = GetLines().ToList();
        var line = lines.SingleOrDefault(l => l.ProductId == productId);
        if (line is null)
        {
            return;
        }

        if (quantity <= 0)
        {
            lines.Remove(line);
        }
        else
        {
            line.Quantity = Math.Min(quantity, MaxQuantityPerLine);
        }

        Save(lines);
    }

    public void Remove(int productId) => SetQuantity(productId, 0);

    public void Clear() => Session.Remove(SessionKey);

    private void Save(List<CartLine> lines) => Session.SetString(SessionKey, JsonSerializer.Serialize(lines));
}
