// File: src/OnlineStoreSystem.Shared/Correlation/CorrelationIdConstants.cs
namespace OnlineStoreSystem.Shared.Correlation;

/// <summary>
/// Names used to carry the correlation id across HTTP, AMQP and logs.
/// </summary>
public static class CorrelationIdConstants
{
    /// <summary>HTTP header (and AMQP header) that carries the correlation id.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Structured logging property name (Serilog / ILogger scopes).</summary>
    public const string LogPropertyName = "CorrelationId";

    /// <summary>Key under which the id is stored in <c>HttpContext.Items</c>.</summary>
    public const string HttpContextItemKey = "OnlineStoreSystem.CorrelationId";

    /// <summary>Upper bound for accepted inbound ids (protects logs and headers).</summary>
    public const int MaxLength = 64;
}
