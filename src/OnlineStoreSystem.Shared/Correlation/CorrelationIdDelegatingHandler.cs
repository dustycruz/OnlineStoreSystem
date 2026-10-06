// File: src/OnlineStoreSystem.Shared/Correlation/CorrelationIdDelegatingHandler.cs
using Microsoft.Extensions.Logging;

namespace OnlineStoreSystem.Shared.Correlation;

/// <summary>
/// Outgoing-HTTP half of correlation: stamps <c>X-Correlation-Id</c> on every request
/// sent through an <c>IHttpClientFactory</c> client. Register it OUTSIDE the resilience
/// handler so every retry attempt carries the same id.
/// </summary>
public sealed class CorrelationIdDelegatingHandler(
    ICorrelationIdAccessor accessor,
    ILogger<CorrelationIdDelegatingHandler> logger) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = accessor.CorrelationId;
        if (string.IsNullOrEmpty(correlationId))
        {
            // Outbound call outside any request/message (e.g. a timer) - start a new trace.
            correlationId = CorrelationIdGenerator.NewId();
            logger.LogDebug("No ambient correlation id; generated {CorrelationId} for outbound call", correlationId);
        }

        request.Headers.Remove(CorrelationIdConstants.HeaderName);
        request.Headers.TryAddWithoutValidation(CorrelationIdConstants.HeaderName, correlationId);

        logger.LogDebug("Propagating {HeaderName}: {CorrelationId} to {Method} {RequestUri}",
            CorrelationIdConstants.HeaderName, correlationId, request.Method, request.RequestUri);

        return base.SendAsync(request, cancellationToken);
    }
}
