// File: src/OnlineStoreSystem.OrderService/Clients/ResiliencePolicies.cs
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace OnlineStoreSystem.OrderService.Clients;

/// <summary>
/// Applies <see cref="HttpResilienceSettings"/> to the standard resilience pipeline of
/// <c>Microsoft.Extensions.Http.Resilience</c>. Pipeline order (outer → inner):
/// <code>
///   rate limiter → TOTAL timeout → RETRY (exponential backoff + jitter) → circuit breaker → ATTEMPT timeout → HTTP
/// </code>
/// Retries happen on HttpRequestException, 5xx, 408, 429 and attempt timeouts. Each retry is logged with
/// the ambient correlation id, so a single <c>grep</c> shows "attempt 1 failed, retry 1 in 512 ms, ...".
/// </summary>
public static class ResiliencePolicies
{
    public static void Apply(
        HttpStandardResilienceOptions options,
        HttpResilienceSettings settings,
        IServiceProvider services,
        string dependencyName)
    {
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger($"OnlineStoreSystem.OrderService.Resilience.{dependencyName}");

        // --- Retry: exponential backoff with jitter ---
        options.Retry.MaxRetryAttempts = settings.MaxRetryAttempts;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = settings.UseJitter;
        options.Retry.Delay = TimeSpan.FromMilliseconds(settings.BaseDelayMilliseconds);
        options.Retry.OnRetry = args =>
        {
            var reason = args.Outcome.Exception is { } exception
                ? $"{exception.GetType().Name}: {exception.Message}"
                : $"HTTP {(int?)args.Outcome.Result?.StatusCode} {args.Outcome.Result?.ReasonPhrase}";

            logger.LogWarning(
                "RESILIENCE: call to {Dependency} failed ({Reason}); retry {Retry}/{MaxRetries} in {DelayMs} ms (exponential backoff + jitter)",
                dependencyName, reason, args.AttemptNumber + 1, settings.MaxRetryAttempts,
                (int)args.RetryDelay.TotalMilliseconds);
            return ValueTask.CompletedTask;
        };

        // --- Timeouts: per attempt and for the whole operation ---
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(settings.TotalTimeoutSeconds);

        // --- Circuit breaker (sampling window must be >= 2x the attempt timeout) ---
        options.CircuitBreaker.FailureRatio = settings.CircuitBreakerFailureRatio;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, settings.AttemptTimeoutSeconds * 2));
        options.CircuitBreaker.MinimumThroughput = 10;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(settings.CircuitBreakerBreakSeconds);
        options.CircuitBreaker.OnOpened = args =>
        {
            logger.LogError("RESILIENCE: circuit to {Dependency} OPENED for {BreakSeconds}s - calls fail fast",
                dependencyName, args.BreakDuration.TotalSeconds);
            return ValueTask.CompletedTask;
        };
        options.CircuitBreaker.OnClosed = _ =>
        {
            logger.LogInformation("RESILIENCE: circuit to {Dependency} CLOSED - traffic flows again", dependencyName);
            return ValueTask.CompletedTask;
        };
    }
}
