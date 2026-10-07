// File: src/OnlineStoreSystem.Shared/DTOs/OrderRequests.cs
using System.ComponentModel.DataAnnotations;
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.Shared.DTOs;

/// <summary>A requested order line (schema <c>OrderLineRequest</c>).</summary>
public sealed record OrderLineRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "ProductId must be a positive integer.")]
    public int ProductId { get; init; }

    [Range(1, 1000, ErrorMessage = "Quantity must be between 1 and 1000.")]
    public int Quantity { get; init; }
}

/// <summary>Body of <c>POST /api/v1/orders</c>: creates a DRAFT order (no saga yet).</summary>
public sealed record CreateOrderRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string CustomerId { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string CustomerEmail { get; init; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = "An order needs at least one item.")]
    [MaxLength(50, ErrorMessage = "An order can contain at most 50 items.")]
    public List<OrderLineRequest> Items { get; init; } = [];
}

/// <summary>Body of <c>PUT /api/v1/orders/{orderId}</c>: replaces a DRAFT order.</summary>
public sealed record UpdateOrderRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string CustomerId { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string CustomerEmail { get; init; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = "An order needs at least one item.")]
    [MaxLength(50, ErrorMessage = "An order can contain at most 50 items.")]
    public List<OrderLineRequest> Items { get; init; } = [];
}

/// <summary>
/// Card data for the checkout (schema <c>PaymentDetails</c>). Never persisted in clear text:
/// OrderService encrypts it with ASP.NET Core Data Protection until the payment step ran.
/// Demo card numbers: 4242424242424242 (approved), 4000000000000002 (declined → compensation).
/// </summary>
public sealed record PaymentDetailsRequest : IValidatableObject
{
    [Required]
    [LuhnCardNumber]
    public string CardNumber { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 2)]
    public string CardHolderName { get; init; } = string.Empty;

    [Range(1, 12)]
    public int ExpiryMonth { get; init; }

    [Range(2000, 2100)]
    public int ExpiryYear { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var now = DateTime.UtcNow;
        if (ExpiryYear < now.Year || (ExpiryYear == now.Year && ExpiryMonth < now.Month))
        {
            yield return new ValidationResult("The card has expired.", [nameof(ExpiryMonth), nameof(ExpiryYear)]);
        }
    }
}

/// <summary>
/// Body of <c>POST /api/v1/orders/checkout</c>: creates the order AND starts the saga in one call.
/// </summary>
public sealed record CheckoutRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string CustomerId { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string CustomerEmail { get; init; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = "An order needs at least one item.")]
    [MaxLength(50, ErrorMessage = "An order can contain at most 50 items.")]
    public List<OrderLineRequest> Items { get; init; } = [];

    [Required]
    public PaymentDetailsRequest? Payment { get; init; }
}

/// <summary>Body of <c>POST /api/v1/orders/{orderId}/checkout</c>: submits an existing DRAFT order.</summary>
public sealed record SubmitOrderRequest
{
    [Required]
    public PaymentDetailsRequest? Payment { get; init; }
}
