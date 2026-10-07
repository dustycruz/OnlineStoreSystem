// File: web/OnlineStoreSystem.Storefront/Models/ViewModels.cs
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.Storefront.Models;

/// <summary>A product tile: catalog data + (optional) stock level from InventoryService.</summary>
public sealed record ProductTileViewModel(ProductDto Product, InventoryItemDto? Stock)
{
    public string StockLabel => Stock switch
    {
        null => "Stock unknown",
        { QuantityAvailable: <= 0 } => "Out of stock",
        { NeedsReorder: true } stock => $"Only {stock.QuantityAvailable} left",
        { } stock => $"{stock.QuantityAvailable} in stock"
    };

    public string StockCss => Stock switch
    {
        null => "text-bg-secondary",
        { QuantityAvailable: <= 0 } => "text-bg-danger",
        { NeedsReorder: true } => "text-bg-warning",
        _ => "text-bg-success"
    };

    public bool CanBuy => Product.IsActive && (Stock is null || Stock.QuantityAvailable > 0);
}

public sealed class HomeViewModel
{
    public IReadOnlyList<ProductTileViewModel> Featured { get; init; } = [];

    public string? ErrorMessage { get; init; }
}

public sealed class CatalogIndexViewModel
{
    public IReadOnlyList<ProductTileViewModel> Products { get; init; } = [];

    public string? Search { get; init; }

    public string? Category { get; init; }

    public int Page { get; init; } = 1;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class CartViewModel
{
    public IReadOnlyList<CartLine> Lines { get; init; } = [];

    public decimal Total => Lines.Sum(line => line.LineTotal);

    public string Currency => Lines.FirstOrDefault()?.Currency ?? "USD";

    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>Checkout form. Demo cards: 4242 4242 4242 4242 (approved), 4000 0000 0000 0002 (declined).</summary>
public sealed class CheckoutViewModel : IValidatableObject
{
    [Required]
    [StringLength(200, MinimumLength = 2)]
    [Display(Name = "Name on card")]
    public string CardHolderName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    [Display(Name = "E-mail")]
    public string Email { get; set; } = string.Empty;

    [Required]
    [LuhnCardNumber]
    [Display(Name = "Card number")]
    public string CardNumber { get; set; } = "4242 4242 4242 4242";

    [Range(1, 12)]
    [Display(Name = "Expiry month")]
    public int ExpiryMonth { get; set; } = 12;

    [Range(2000, 2100)]
    [Display(Name = "Expiry year")]
    public int ExpiryYear { get; set; } = DateTime.UtcNow.Year + 3;

    [BindNever]
    public CartViewModel Cart { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var now = DateTime.UtcNow;
        if (ExpiryYear < now.Year || (ExpiryYear == now.Year && ExpiryMonth < now.Month))
        {
            yield return new ValidationResult("The card has expired.", [nameof(ExpiryMonth)]);
        }
    }
}

public sealed class OrderStatusViewModel
{
    public required OrderDto Order { get; init; }

    public bool IsFinal =>
        OrderStatuses.IsTerminal(Order.Status) && (Order.Saga is null || SagaStatuses.IsTerminal(Order.Saga.Status));

    public int? AutoRefreshSeconds => IsFinal ? null : 2;

    public string StatusCss => StatusBadge(Order.Status);

    public static string StatusBadge(string status) => status switch
    {
        OrderStatuses.Completed => "text-bg-success",
        OrderStatuses.Cancelled => "text-bg-danger",
        OrderStatuses.StockReserved => "text-bg-info",
        OrderStatuses.Pending => "text-bg-warning",
        _ => "text-bg-secondary"
    };

    public static string OutcomeCss(string outcome) => outcome switch
    {
        SagaStepOutcomes.Succeeded => "list-group-item-success",
        SagaStepOutcomes.Failed => "list-group-item-danger",
        SagaStepOutcomes.Compensating or SagaStepOutcomes.Compensated => "list-group-item-warning",
        SagaStepOutcomes.Retrying => "list-group-item-info",
        _ => string.Empty
    };
}

public sealed class OrdersIndexViewModel
{
    public IReadOnlyList<OrderSummaryDto> Orders { get; init; } = [];

    public string CustomerId { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }
}

public sealed class ErrorViewModel
{
    public string? RequestId { get; init; }

    public string? CorrelationId { get; init; }

    public string? Message { get; init; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
