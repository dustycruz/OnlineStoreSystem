// File: src/OnlineStoreSystem.Shared/Validation/LuhnCardNumberAttribute.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.Shared.Validation;

/// <summary>
/// Validates a payment card number: 12-19 digits (spaces / dashes ignored) passing the Luhn checksum.
/// Test numbers such as 4242 4242 4242 4242 and 4000 0000 0000 0002 are valid.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class LuhnCardNumberAttribute : ValidationAttribute
{
    public LuhnCardNumberAttribute()
        : base("The {0} field is not a valid card number.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is null)
        {
            return true; // [Required] decides about null.
        }

        return value is string text && IsValidCardNumber(text);
    }

    public static string Normalize(string cardNumber) =>
        new(cardNumber.Where(c => c is not (' ' or '-')).ToArray());

    public static bool IsValidCardNumber(string cardNumber)
    {
        var digits = Normalize(cardNumber);
        if (digits.Length is < 12 or > 19 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
