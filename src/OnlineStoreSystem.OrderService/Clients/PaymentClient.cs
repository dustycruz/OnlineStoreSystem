// File: src/OnlineStoreSystem.OrderService/Clients/PaymentClient.cs
using System.Net;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Shared.Http;
using OnlineStoreSystem.Shared.Json;

namespace OnlineStoreSystem.OrderService.Clients;

/// <summary>Outcome of the saga's "Process Payment" step.</summary>
public sealed record PaymentOutcome(
    bool IsApproved,
    PaymentDto? Payment,
    Guid? PaymentId,
    string? DeclineReason,
    string? DeclineCode,
    bool Replayed)
{
    public static PaymentOutcome Approved(PaymentDto payment, bool replayed) =>
        new(true, payment, payment.PaymentId, null, null, replayed);

    public static PaymentOutcome Declined(string reason, string? declineCode, Guid? paymentId, bool replayed) =>
        new(false, null, paymentId, reason, declineCode, replayed);
}

/// <summary>
/// Typed, resilient HTTP client for PaymentService. Every charge carries an <c>Idempotency-Key</c>
/// (<c>order-{orderId}-charge</c>) so the resilience pipeline - and the saga recovery worker - can safely
/// retry a POST without ever charging the customer twice.
/// </summary>
public sealed class PaymentClient(HttpClient httpClient, ILogger<PaymentClient> logger)
{
    public const string DependencyName = "PaymentService";

    public async Task<PaymentOutcome> ChargeAsync(
        ChargePaymentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/payments/charge")
        {
            Content = JsonContent.Create(request, options: JsonDefaults.Web)
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        logger.LogInformation("Charging {Amount} {Currency} for order {OrderId} via {Dependency} (Idempotency-Key {IdempotencyKey})",
            request.Amount, request.Currency, request.OrderId, DependencyName, idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (CatalogClient.IsTransientFailure(ex, cancellationToken))
        {
            logger.LogError("{Dependency} unreachable after all retries: {Error}", DependencyName, ex.Message);
            throw new DependencyUnavailableException(DependencyName,
                $"{DependencyName} could not be reached ({ex.GetType().Name}: {ex.Message}).", ex);
        }

        using (response)
        {
            var replayed = response.Headers.TryGetValues("Idempotent-Replayed", out var values)
                           && values.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

            switch (response.StatusCode)
            {
                case HttpStatusCode.Created:
                case HttpStatusCode.OK:
                {
                    var payment = await response.Content.ReadFromJsonAsync<PaymentDto>(JsonDefaults.Web, cancellationToken)
                        ?? throw new InvalidOperationException($"{DependencyName} returned an empty payment.");
                    logger.LogInformation("Payment {PaymentId} approved for order {OrderId}{Replay}",
                        payment.PaymentId, request.OrderId, replayed ? " (idempotent replay)" : string.Empty);
                    return PaymentOutcome.Approved(payment, replayed);
                }

                case HttpStatusCode.PaymentRequired:
                {
                    var problem = await response.ReadProblemAsync(cancellationToken);
                    var paymentId = Guid.TryParse(problem?.GetExtensionString("paymentId"), out var id) ? id : (Guid?)null;
                    var declineCode = problem?.GetExtensionString("declineCode");
                    var reason = problem?.Detail ?? "The payment was declined.";
                    logger.LogWarning("Payment for order {OrderId} DECLINED ({DeclineCode}): {Reason}",
                        request.OrderId, declineCode, reason);
                    return PaymentOutcome.Declined(reason, declineCode, paymentId, replayed);
                }

                case HttpStatusCode.BadRequest:
                case HttpStatusCode.Conflict:
                case HttpStatusCode.UnprocessableEntity:
                {
                    var problem = await response.ReadProblemAsync(cancellationToken);
                    var errors = problem?.Errors is { Count: > 0 } validationErrors
                        ? " " + string.Join("; ", validationErrors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"))
                        : string.Empty;
                    var reason = $"Payment request rejected ({(int)response.StatusCode}): {problem?.Detail ?? problem?.Title}{errors}";
                    logger.LogWarning("Payment for order {OrderId} rejected: {Reason}", request.OrderId, reason);
                    return PaymentOutcome.Declined(reason, "invalid_request", null, replayed);
                }

                default:
                    if (CatalogClient.IsTransientStatus(response.StatusCode))
                    {
                        logger.LogError("{Dependency} still answered {StatusCode} after all retries", DependencyName,
                            (int)response.StatusCode);
                        throw new DependencyUnavailableException(DependencyName,
                            $"{DependencyName} answered {(int)response.StatusCode} {response.ReasonPhrase} after all retries.");
                    }

                    throw new InvalidOperationException(
                        $"{DependencyName} answered unexpected status {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
        }
    }
}
