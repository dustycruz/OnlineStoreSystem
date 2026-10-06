// File: src/OnlineStoreSystem.Shared/Correlation/CorrelationIdMiddleware.cs
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OnlineStoreSystem.Shared.Correlation;

/// <summary>
/// Edge + hop middleware for <c>X-Correlation-Id</c>:
/// <list type="number">
///   <item>Reuses a valid inbound header, otherwise generates a new id (edge of the system).</item>
///   <item>Stores it in <see cref="ICorrelationIdAccessor"/> and <c>HttpContext.Items</c>.</item>
///   <item>Echoes it on the response header.</item>
///   <item>Opens an <see cref="ILogger"/> scope so EVERY log line of the request carries it.</item>
/// </list>
/// Register it as the first middleware so exception handling and request logging run inside the scope.
/// </summary>
public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ICorrelationIdAccessor accessor,
    ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[CorrelationIdConstants.HeaderName].FirstOrDefault();
        var hasValidInbound = CorrelationIdGenerator.IsValid(inbound);
        var correlationId = hasValidInbound ? inbound! : CorrelationIdGenerator.NewId();

        context.Items[CorrelationIdConstants.HttpContextItemKey] = correlationId;
        accessor.CorrelationId = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        context.Response.OnStarting(static state =>
        {
            var (httpContext, id) = ((HttpContext, string))state;
            httpContext.Response.Headers[CorrelationIdConstants.HeaderName] = id;
            return Task.CompletedTask;
        }, (context, correlationId));

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   [CorrelationIdConstants.LogPropertyName] = correlationId
               }))
        {
            if (!hasValidInbound && !string.IsNullOrEmpty(inbound))
            {
                // Never log the raw rejected value (log-injection safe).
                logger.LogWarning(
                    "Rejected malformed {HeaderName} header (length {Length}); generated a new correlation id",
                    CorrelationIdConstants.HeaderName, inbound.Length);
            }
            else if (!hasValidInbound)
            {
                logger.LogDebug("No {HeaderName} header supplied; generated {CorrelationId} at the edge",
                    CorrelationIdConstants.HeaderName, correlationId);
            }

            try
            {
                await next(context);
            }
            finally
            {
                accessor.CorrelationId = null;
            }
        }
    }
}
