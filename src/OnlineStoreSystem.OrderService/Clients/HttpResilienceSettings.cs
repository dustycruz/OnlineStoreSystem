// File: src/OnlineStoreSystem.OrderService/Clients/HttpResilienceSettings.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.OrderService.Clients;

/// <summary>
/// Tunables of the standard HTTP resilience pipeline for one downstream dependency
/// (sections <c>Resilience:Catalog</c> and <c>Resilience:Payment</c>).
/// </summary>
public sealed class HttpResilienceSettings
{
    /// <summary>Retries after the first attempt (total attempts = 1 + MaxRetryAttempts).</summary>
    [Range(1, 10)]
    public int MaxRetryAttempts { get; set; } = 4;

    /// <summary>First backoff delay; doubles on every retry (exponential), randomised by jitter.</summary>
    [Range(50, 30_000)]
    public int BaseDelayMilliseconds { get; set; } = 500;

    public bool UseJitter { get; set; } = true;

    /// <summary>Timeout of ONE attempt.</summary>
    [Range(1, 120)]
    public int AttemptTimeoutSeconds { get; set; } = 5;

    /// <summary>Timeout of the WHOLE call including all retries and backoff delays.</summary>
    [Range(2, 600)]
    public int TotalTimeoutSeconds { get; set; } = 30;

    /// <summary>Circuit breaker opens when this ratio of calls fails within the sampling window.</summary>
    [Range(0.01, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    [Range(1, 600)]
    public int CircuitBreakerBreakSeconds { get; set; } = 15;
}
