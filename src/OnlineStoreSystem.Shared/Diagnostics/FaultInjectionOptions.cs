// File: src/OnlineStoreSystem.Shared/Diagnostics/FaultInjectionOptions.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.Shared.Diagnostics;

/// <summary>
/// Deterministic transient-fault injection used to DEMONSTRATE the HTTP resilience pipeline.
/// When enabled, the first <see cref="FailuresPerCorrelationId"/> requests that carry the same
/// <c>X-Correlation-Id</c> are answered with <see cref="StatusCode"/>; later attempts succeed.
/// Because the caller's retries keep the same correlation id, logs show
/// "503, 503, 200" with exponential backoff between the attempts.
/// Disabled by default; enable with e.g. <c>--FaultInjection:Enabled=true</c>.
/// </summary>
public sealed class FaultInjectionOptions
{
    public const string SectionName = "FaultInjection";

    public bool Enabled { get; set; }

    [Range(1, 20)]
    public int FailuresPerCorrelationId { get; set; } = 2;

    [Range(500, 599)]
    public int StatusCode { get; set; } = 503;

    /// <summary>Only requests whose path starts with this prefix are affected.</summary>
    [Required]
    public string PathPrefix { get; set; } = "/api/";
}
