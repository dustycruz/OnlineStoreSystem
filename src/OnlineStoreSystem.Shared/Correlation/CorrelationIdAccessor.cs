// File: src/OnlineStoreSystem.Shared/Correlation/CorrelationIdAccessor.cs
namespace OnlineStoreSystem.Shared.Correlation;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed accessor: the value flows with the async
/// execution context, so every <c>await</c> below the point where it was set
/// (controllers, EF Core calls, HttpClient handlers, publishers) observes it.
/// </summary>
public sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? CorrelationId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
