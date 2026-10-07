// File: src/OnlineStoreSystem.CatalogService/Mapping/ProductMappings.cs
using System.Linq.Expressions;
using OnlineStoreSystem.CatalogService.Models;
using OnlineStoreSystem.Shared.DTOs;

namespace OnlineStoreSystem.CatalogService.Mapping;

/// <summary>Entity ⇄ DTO mapping. Scaffolded entities never leave the service boundary.</summary>
public static class ProductMappings
{
    /// <summary>Server-side projection (translated to SQL; avoids loading RowVersion etc.).</summary>
    public static Expression<Func<Product, ProductDto>> Projection { get; } = p => new ProductDto(
        p.ProductId,
        p.Sku,
        p.Name,
        p.Description,
        p.Category,
        p.Price,
        p.Currency,
        p.IsActive,
        p.CreatedAtUtc,
        p.UpdatedAtUtc);

    private static readonly Func<Product, ProductDto> CompiledProjection = Projection.Compile();

    public static ProductDto ToDto(this Product product) => CompiledProjection(product);

    public static void Apply(this Product product, CreateProductRequest request, DateTime nowUtc)
    {
        product.Sku = request.Sku.Trim();
        product.Name = request.Name.Trim();
        product.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        product.Category = request.Category.Trim();
        product.Price = request.Price;
        product.Currency = request.Currency;
        product.IsActive = request.IsActive;
        product.CreatedAtUtc = nowUtc;
    }

    public static void Apply(this Product product, UpdateProductRequest request, DateTime nowUtc)
    {
        product.Sku = request.Sku.Trim();
        product.Name = request.Name.Trim();
        product.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        product.Category = request.Category.Trim();
        product.Price = request.Price;
        product.Currency = request.Currency;
        product.IsActive = request.IsActive;
        product.UpdatedAtUtc = nowUtc;
    }
}
