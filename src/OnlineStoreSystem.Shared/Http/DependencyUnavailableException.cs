// File: src/OnlineStoreSystem.Shared/Http/DependencyUnavailableException.cs
namespace OnlineStoreSystem.Shared.Http;

/// <summary>
/// Raised by typed HTTP clients when a downstream service could not be reached even after the
/// resilience pipeline (retries with exponential backoff + jitter, attempt and total timeouts,
/// circuit breaker) gave up. Mapped to <c>503 Service Unavailable</c> problem details.
/// </summary>
public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException(string dependency, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Dependency = dependency;
    }

    /// <summary>Logical name of the dependency, e.g. "CatalogService".</summary>
    public string Dependency { get; }
}
