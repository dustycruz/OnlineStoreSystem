// File: src/OnlineStoreSystem.Shared/Validation/ValidationPatterns.cs
namespace OnlineStoreSystem.Shared.Validation;

/// <summary>Regular expressions shared by DTO validation attributes and the OpenAPI contracts.</summary>
public static class ValidationPatterns
{
    /// <summary>ISO 4217 alphabetic currency code, upper case.</summary>
    public const string CurrencyCode = "^[A-Z]{3}$";

    /// <summary>Stock keeping unit: letters, digits and dashes.</summary>
    public const string Sku = "^[A-Za-z0-9][A-Za-z0-9-]{0,63}$";
}
