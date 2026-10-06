// File: src/OnlineStoreSystem.PaymentService/Services/SimulatedPaymentGateway.cs
using Microsoft.Extensions.Options;
using OnlineStoreSystem.Shared.Contracts.Payments;

namespace OnlineStoreSystem.PaymentService.Services;

/// <summary>Result of an authorization at the (simulated) card network.</summary>
public sealed record GatewayDecision(bool Approved, string? DeclineCode, string? Reason)
{
    public static GatewayDecision Approve() => new(true, null, null);

    public static GatewayDecision Decline(string code, string reason) => new(false, code, reason);
}

public interface IPaymentGateway
{
    Task<GatewayDecision> AuthorizeAsync(string cardNumber, decimal amount, string currency, CancellationToken cancellationToken);
}

public sealed class PaymentGatewayOptions
{
    public const string SectionName = "PaymentGateway";

    /// <summary>Artificial network latency of the simulated acquirer.</summary>
    public int SimulatedLatencyMilliseconds { get; set; } = 150;
}

/// <summary>
/// Deterministic stand-in for a real PSP so the saga's compensation path can be triggered on demand:
/// <list type="bullet">
///   <item>card <c>4000000000000002</c> → declined (<c>card_declined</c>)</item>
///   <item>card <c>4000000000009995</c> → declined (<c>insufficient_funds</c>)</item>
///   <item>amount exactly <c>9999.00</c> → declined (<c>amount_limit_exceeded</c>)</item>
///   <item>anything else (e.g. <c>4242424242424242</c>) → approved</item>
/// </list>
/// </summary>
public sealed class SimulatedPaymentGateway(
    IOptions<PaymentGatewayOptions> options,
    ILogger<SimulatedPaymentGateway> logger) : IPaymentGateway
{
    public async Task<GatewayDecision> AuthorizeAsync(
        string cardNumber, decimal amount, string currency, CancellationToken cancellationToken)
    {
        var latency = options.Value.SimulatedLatencyMilliseconds;
        if (latency > 0)
        {
            await Task.Delay(latency, cancellationToken);
        }

        GatewayDecision decision = cardNumber switch
        {
            PaymentTestValues.DeclinedCard => GatewayDecision.Decline(
                DeclineCodes.CardDeclined,
                "The card was declined by the issuer (test card 4000 0000 0000 0002)."),
            PaymentTestValues.InsufficientFundsCard => GatewayDecision.Decline(
                DeclineCodes.InsufficientFunds,
                "Insufficient funds on the card (test card 4000 0000 0000 9995)."),
            _ when amount == PaymentTestValues.DeclinedAmount => GatewayDecision.Decline(
                DeclineCodes.AmountLimitExceeded,
                $"Charges of exactly {PaymentTestValues.DeclinedAmount:F2} {currency} are declined (deterministic saga-compensation trigger)."),
            _ => GatewayDecision.Approve()
        };

        logger.LogInformation("Gateway decision for {Amount} {Currency} on card ending {Last4}: {Decision}",
            amount, currency, cardNumber[^4..], decision.Approved ? "APPROVED" : $"DECLINED ({decision.DeclineCode})");

        return decision;
    }
}
