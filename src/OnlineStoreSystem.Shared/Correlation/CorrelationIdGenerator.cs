// File: src/OnlineStoreSystem.Shared/Correlation/CorrelationIdGenerator.cs
namespace OnlineStoreSystem.Shared.Correlation;

/// <summary>
/// Creates and validates correlation ids. Inbound ids are only trusted when they are
/// short and made of safe characters, which prevents header/log injection.
/// </summary>
public static class CorrelationIdGenerator
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CorrelationIdConstants.MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns <paramref name="candidate"/> when valid, otherwise a fresh id.</summary>
    public static string Sanitize(string? candidate) => IsValid(candidate) ? candidate! : NewId();
}
