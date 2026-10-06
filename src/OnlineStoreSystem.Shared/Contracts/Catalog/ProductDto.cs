// File: src/OnlineStoreSystem.Shared/Contracts/Catalog/ProductDto.cs
namespace OnlineStoreSystem.Shared.Contracts.Catalog;

/// <summary>Product representation returned by CatalogService (schema <c>Product</c> in catalog-v1.yaml).</summary>
public sealed record ProductDto(
    int ProductId,
    string Sku,
    string Name,
    string? Description,
    string Category,
    decimal Price,
    string Currency,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);
