// File: src/OnlineStoreSystem.Shared/Events/PaymentFailedEvent.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Emitted by the saga orchestrator when the "Process Payment" step fails. It is the trigger of
/// the <b>ReleaseStock compensation</b>: InventoryService releases every reservation of the order
/// and answers with <see cref="StockReleasedEvent"/>.
/// Routing key: <c>payment.failed</c>.
/// </summary>
public sealed record PaymentFailedEvent : IntegrationEvent
{
    public required Guid OrderId { get; init; }

    /// <summary>The declined transaction id, when PaymentService recorded one.</summary>
    public Guid? PaymentId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string Reason { get; init; }

    public string? DeclineCode { get; init; }
}
