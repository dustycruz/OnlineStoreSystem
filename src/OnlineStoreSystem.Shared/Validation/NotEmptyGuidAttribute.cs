// File: src/OnlineStoreSystem.Shared/Validation/NotEmptyGuidAttribute.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.Shared.Validation;

/// <summary>Rejects <see cref="Guid.Empty"/> (which <c>[Required]</c> lets through for value types).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NotEmptyGuidAttribute : ValidationAttribute
{
    public NotEmptyGuidAttribute()
        : base("The {0} field must be a non-empty GUID.")
    {
    }

    public override bool IsValid(object? value) => value is not Guid guid || guid != Guid.Empty;
}
