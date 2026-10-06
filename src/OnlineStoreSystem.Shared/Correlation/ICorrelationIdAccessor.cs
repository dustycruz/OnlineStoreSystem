// File: src/OnlineStoreSystem.Shared/Correlation/ICorrelationIdAccessor.cs
namespace OnlineStoreSystem.Shared.Correlation;

/// <summary>
/// Ambient access to the correlation id of the current logical operation.
/// Works for HTTP requests (set by <see cref="CorrelationIdMiddleware"/>) and for
/// RabbitMQ deliveries / background work (set by the consumer base class), which is
/// why it is not based on <c>IHttpContextAccessor</c>.
/// </summary>
public interface ICorrelationIdAccessor
{
    string? CorrelationId { get; set; }
}
