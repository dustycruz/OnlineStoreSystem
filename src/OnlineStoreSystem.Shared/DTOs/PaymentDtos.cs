// File: src/OnlineStoreSystem.Shared/DTOs/PaymentDtos.cs
using System.ComponentModel.DataAnnotations;
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.Shared.DTOs;

/// <summary>Values of <c>PaymentDb.PaymentTransactions.Status</c>.</summary>
public static class PaymentStatuses
{
    public const string Captured = "Captured";
    public const string Declined = "Declined";
    public const string PartiallyRefunded = "PartiallyRefunded";
    public const string Refunded = "Refunded";
}

/// <summary>Deterministic triggers of the simulated payment gateway (used to demo compensation).</summary>
public static class PaymentTestValues
{
    /// <summary>Always approved.</summary>
    public const string ApprovedCard = "4242424242424242";

    /// <summary>Always declined with <c>card_declined</c>.</summary>
    public const string DeclinedCard = "4000000000000002";

    /// <summary>Always declined with <c>insufficient_funds</c>.</summary>
    public const string InsufficientFundsCard = "4000000000009995";

    /// <summary>Any charge of exactly this amount is declined with <c>amount_limit_exceeded</c>.</summary>
    public const decimal DeclinedAmount = 9999.00m;
}

/// <summary>Decline reason codes returned in the 402 problem details (<c>declineCode</c> extension).</summary>
public static class DeclineCodes
{
    public const string CardDeclined = "card_declined";
    public const string InsufficientFunds = "insufficient_funds";
    public const string AmountLimitExceeded = "amount_limit_exceeded";
}

/// <summary>Body of <c>POST /api/v1/payments/charge</c> (schema <c>ChargeRequest</c>).</summary>
public sealed record ChargePaymentRequest : IValidatableObject
{
    [NotEmptyGuid]
    public Guid OrderId { get; init; }

    [Range(0.01, 1_000_000_000.0)]
    public decimal Amount { get; init; }

    [Required]
    [RegularExpression(ValidationPatterns.CurrencyCode, ErrorMessage = "Currency must be a 3-letter upper-case ISO 4217 code.")]
    public string Currency { get; init; } = "USD";

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

        if (decimal.Round(Amount, 2) != Amount)
        {
            yield return new ValidationResult("Amount must have at most two decimal places.", [nameof(Amount)]);
        }
    }
}

/// <summary>Body of <c>POST /api/v1/payments/{paymentId}/refunds</c> (schema <c>RefundRequest</c>).</summary>
public sealed record RefundPaymentRequest
{
    /// <summary>Amount to refund; omit for a full refund of the remaining balance.</summary>
    [Range(0.01, 1_000_000_000.0)]
    public decimal? Amount { get; init; }

    [StringLength(500)]
    public string? Reason { get; init; }
}

/// <summary>A payment transaction (schema <c>Payment</c>).</summary>
public sealed record PaymentDto(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string Status,
    string CardBrand,
    string CardLast4,
    string? DeclineCode,
    string? FailureReason,
    decimal RefundedAmount,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    IReadOnlyList<RefundDto> Refunds);

/// <summary>A refund of a payment (schema <c>Refund</c>).</summary>
public sealed record RefundDto(Guid RefundId, decimal Amount, string? Reason, DateTime CreatedAtUtc);
