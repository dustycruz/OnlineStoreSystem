// File: src/OnlineStoreSystem.OrderService/Saga/SagaOptions.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.OrderService.Saga;

/// <summary>Timeouts and retry budgets of the checkout saga (section <c>Saga</c>).</summary>
public sealed class SagaOptions
{
    public const string SectionName = "Saga";

    /// <summary>How long to wait for InventoryService's reply before cancelling the order.</summary>
    [Range(5, 86_400)]
    public int StockReservationTimeoutSeconds { get; set; } = 120;

    /// <summary>Delay before the recovery worker retries a payment step that is stuck / failed transiently.
    /// Must be larger than the Payment HTTP client's total timeout.</summary>
    [Range(5, 86_400)]
    public int PaymentRetryIntervalSeconds { get; set; } = 30;

    /// <summary>Attempts (including the first) to reach PaymentService before compensating.</summary>
    [Range(1, 20)]
    public int MaxPaymentAttempts { get; set; } = 3;

    /// <summary>Delay before re-sending an unconfirmed compensation.</summary>
    [Range(5, 86_400)]
    public int CompensationRetryIntervalSeconds { get; set; } = 60;

    [Range(1, 50)]
    public int MaxCompensationAttempts { get; set; } = 5;

    /// <summary>How often the recovery worker scans for stalled sagas.</summary>
    [Range(1, 3_600)]
    public int RecoveryPollIntervalSeconds { get; set; } = 10;
}
