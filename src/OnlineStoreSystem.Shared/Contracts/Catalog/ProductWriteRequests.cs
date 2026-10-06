// File: src/OnlineStoreSystem.Shared/Contracts/Catalog/ProductWriteRequests.cs
using System.ComponentModel.DataAnnotations;
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.Shared.Contracts.Catalog;

/// <summary>Body of <c>POST /api/v1/products</c> (schema <c>CreateProductRequest</c>).</summary>
public sealed record CreateProductRequest
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.Sku, ErrorMessage = "Sku may contain only letters, digits and dashes.")]
    public string Sku { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Category { get; init; } = "General";

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; init; }

    [Required]
    [RegularExpression(ValidationPatterns.CurrencyCode, ErrorMessage = "Currency must be a 3-letter upper-case ISO 4217 code.")]
    public string Currency { get; init; } = "USD";

    public bool IsActive { get; init; } = true;
}

/// <summary>Body of <c>PUT /api/v1/products/{productId}</c> (full replacement, schema <c>UpdateProductRequest</c>).</summary>
public sealed record UpdateProductRequest
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.Sku, ErrorMessage = "Sku may contain only letters, digits and dashes.")]
    public string Sku { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Category { get; init; } = "General";

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; init; }

    [Required]
    [RegularExpression(ValidationPatterns.CurrencyCode, ErrorMessage = "Currency must be a 3-letter upper-case ISO 4217 code.")]
    public string Currency { get; init; } = "USD";

    public bool IsActive { get; init; } = true;
}
