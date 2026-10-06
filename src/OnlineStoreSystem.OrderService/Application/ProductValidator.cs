// File: src/OnlineStoreSystem.OrderService/Application/ProductValidator.cs
using OnlineStoreSystem.OrderService.Clients;
using OnlineStoreSystem.Shared.Contracts.Orders;

namespace OnlineStoreSystem.OrderService.Application;

/// <summary>An order line priced with the authoritative catalog data.</summary>
public sealed record PricedLine(int ProductId, string Sku, string ProductName, decimal UnitPrice, int Quantity);

public sealed record ProductValidationResult(
    IReadOnlyList<PricedLine> Lines,
    string Currency,
    IDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public decimal TotalAmount => Lines.Sum(line => line.UnitPrice * line.Quantity);

    public string Describe() =>
        string.Join(", ", Lines.Select(line => $"{line.Quantity} x {line.Sku} @ {line.UnitPrice:F2}"));
}

/// <summary>
/// Saga step 1 - <b>Validate Product (synchronous)</b>. Calls CatalogService through the resilient
/// <see cref="CatalogClient"/>, rejects unknown / inactive products and prices every line with the catalog price
/// (clients never dictate prices). Duplicate product lines are merged.
/// </summary>
public sealed class ProductValidator(CatalogClient catalogClient, ILogger<ProductValidator> logger)
{
    private const int MaxQuantityPerProduct = 1000;

    public async Task<ProductValidationResult> ValidateAsync(
        IReadOnlyList<OrderLineRequest> items,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var products = await catalogClient.GetProductsAsync(items.Select(i => i.ProductId).ToList(), cancellationToken);

        for (var index = 0; index < items.Count; index++)
        {
            var productId = items[index].ProductId;
            if (!products.TryGetValue(productId, out var product))
            {
                errors[$"items[{index}].productId"] = [$"Product {productId} does not exist in the catalog."];
            }
            else if (!product.IsActive)
            {
                errors[$"items[{index}].productId"] = [$"Product {productId} ({product.Sku}) is no longer sold."];
            }
        }

        var merged = items
            .GroupBy(item => item.ProductId)
            .Select(group => (ProductId: group.Key, Quantity: group.Sum(item => item.Quantity)))
            .OrderBy(line => line.ProductId)
            .ToList();

        foreach (var line in merged.Where(line => line.Quantity > MaxQuantityPerProduct))
        {
            errors[$"items[productId={line.ProductId}].quantity"] =
                [$"At most {MaxQuantityPerProduct} units of product {line.ProductId} can be ordered."];
        }

        var currencies = merged
            .Where(line => products.ContainsKey(line.ProductId))
            .Select(line => products[line.ProductId].Currency)
            .Distinct()
            .ToList();
        if (currencies.Count > 1)
        {
            errors["items"] = [$"All products of an order must share one currency (found {string.Join(", ", currencies)})."];
        }

        if (errors.Count > 0)
        {
            logger.LogWarning("Product validation failed: {Errors}",
                string.Join(" | ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}")));
            return new ProductValidationResult([], currencies.FirstOrDefault() ?? "USD", errors);
        }

        var lines = merged
            .Select(line =>
            {
                var product = products[line.ProductId];
                return new PricedLine(product.ProductId, product.Sku, product.Name, product.Price, line.Quantity);
            })
            .ToList();

        var result = new ProductValidationResult(lines, currencies.Single(), errors);
        logger.LogInformation("Product validation succeeded: {Lines} = {Total:F2} {Currency}",
            result.Describe(), result.TotalAmount, result.Currency);
        return result;
    }
}
