// File: src/OnlineStoreSystem.PaymentService/Services/CardInfo.cs
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.PaymentService.Services;

/// <summary>The only card data PaymentService is allowed to keep: brand and last four digits.</summary>
public sealed record CardInfo(string Brand, string Last4)
{
    public static CardInfo From(string cardNumber)
    {
        var digits = LuhnCardNumberAttribute.Normalize(cardNumber);
        return new CardInfo(DetectBrand(digits), digits[^4..]);
    }

    private static string DetectBrand(string digits)
    {
        if (digits.StartsWith('4'))
        {
            return "Visa";
        }

        if (digits.Length >= 2 && digits[..2] is "34" or "37")
        {
            return "Amex";
        }

        if (digits.Length >= 2 && int.TryParse(digits[..2], out var two) && two is >= 51 and <= 55)
        {
            return "Mastercard";
        }

        if (digits.Length >= 4 && int.TryParse(digits[..4], out var four) && four is >= 2221 and <= 2720)
        {
            return "Mastercard";
        }

        if (digits.StartsWith("6011", StringComparison.Ordinal) || digits.StartsWith("65", StringComparison.Ordinal))
        {
            return "Discover";
        }

        return "Unknown";
    }
}
