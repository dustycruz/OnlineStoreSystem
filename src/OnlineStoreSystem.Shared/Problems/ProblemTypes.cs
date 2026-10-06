// File: src/OnlineStoreSystem.Shared/Problems/ProblemTypes.cs
namespace OnlineStoreSystem.Shared.Problems;

/// <summary>
/// RFC 7807 / RFC 9457 <c>type</c> URIs. Generic HTTP problems reference RFC 9110; domain problems use
/// stable URNs that are documented in the OpenAPI contracts under <c>/contracts</c>.
/// </summary>
public static class ProblemTypes
{
    public const string Validation = "https://tools.ietf.org/html/rfc9110#section-15.5.1";
    public const string NotFound = "https://tools.ietf.org/html/rfc9110#section-15.5.5";
    public const string Conflict = "https://tools.ietf.org/html/rfc9110#section-15.5.10";
    public const string UnprocessableContent = "https://tools.ietf.org/html/rfc9110#section-15.5.21";
    public const string InternalError = "https://tools.ietf.org/html/rfc9110#section-15.6.1";

    public const string PaymentDeclined = "urn:onlinestoresystem:problems:payment-declined";
    public const string InsufficientStock = "urn:onlinestoresystem:problems:insufficient-stock";
    public const string DependencyUnavailable = "urn:onlinestoresystem:problems:dependency-unavailable";
    public const string InvalidStateTransition = "urn:onlinestoresystem:problems:invalid-state-transition";
    public const string IdempotencyKeyReused = "urn:onlinestoresystem:problems:idempotency-key-reused";
    public const string ConcurrencyConflict = "urn:onlinestoresystem:problems:concurrency-conflict";
    public const string FaultInjected = "urn:onlinestoresystem:problems:fault-injected";
}
