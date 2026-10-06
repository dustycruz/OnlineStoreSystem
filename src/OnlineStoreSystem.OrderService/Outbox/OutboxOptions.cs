// File: src/OnlineStoreSystem.OrderService/Outbox/OutboxOptions.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.OrderService.Outbox;

/// <summary>Section <c>Outbox</c>.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Fallback polling interval (the relay is also woken immediately after each commit).</summary>
    [Range(100, 60_000)]
    public int PollIntervalMilliseconds { get; set; } = 2000;

    /// <summary>Delay after a failed publish (e.g. broker down) before retrying.</summary>
    [Range(100, 300_000)]
    public int ErrorBackoffMilliseconds { get; set; } = 5000;

    [Range(1, 500)]
    public int BatchSize { get; set; } = 50;
}
