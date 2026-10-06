// File: src/OnlineStoreSystem.Shared/Messaging/PoisonMessageException.cs
namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Thrown when a delivery can NEVER be processed successfully (malformed JSON, unknown type,
/// business-invalid payload). The consumer base class answers it with
/// <c>BasicNack(requeue: false)</c>, which routes the message to the dead-letter queue.
/// </summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException(string message)
        : base(message)
    {
    }

    public PoisonMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
