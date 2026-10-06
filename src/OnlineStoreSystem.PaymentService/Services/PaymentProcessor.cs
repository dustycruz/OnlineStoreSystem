// File: src/OnlineStoreSystem.PaymentService/Services/PaymentProcessor.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.PaymentService.Data;
using OnlineStoreSystem.PaymentService.Data.Entities;
using OnlineStoreSystem.Shared.Contracts.Payments;
using OnlineStoreSystem.Shared.Identifiers;
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.PaymentService.Services;

public enum ChargeStatus
{
    Captured,
    Declined,
    IdempotencyKeyReused
}

public sealed record ChargeOutcome(ChargeStatus Status, PaymentTransaction Transaction, bool Replayed);

public enum RefundStatus
{
    Refunded,
    NotFound,
    NotRefundable,
    InvalidAmount
}

public sealed record RefundOutcome(RefundStatus Status, PaymentTransaction? Transaction, string? Detail);

/// <summary>
/// Saga participant for the <b>Process Payment</b> step.
/// <para>
/// Idempotency: callers send an <c>Idempotency-Key</c> header (OrderService uses
/// <c>order-{orderId}-charge</c>). The first request with a key is executed and its outcome stored; any
/// retry with the same key (e.g. after a timeout in the caller's resilience pipeline) gets the stored outcome
/// back instead of charging the card twice. Reusing a key with a different payload is rejected.
/// </para>
/// </summary>
public sealed class PaymentProcessor(
    PaymentDbContext db,
    IPaymentGateway gateway,
    TimeProvider clock,
    ILogger<PaymentProcessor> logger)
{
    public async Task<ChargeOutcome> ChargeAsync(
        ChargePaymentRequest request,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var cardNumber = LuhnCardNumberAttribute.Normalize(request.CardNumber);
        var card = CardInfo.From(cardNumber);
        var requestHash = ComputeRequestHash(request, card);

        if (idempotencyKey is not null)
        {
            var existing = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return Replay(existing, requestHash, idempotencyKey);
            }
        }

        var decision = await gateway.AuthorizeAsync(cardNumber, request.Amount, request.Currency, cancellationToken);

        var transaction = new PaymentTransaction
        {
            PaymentId = SequentialGuid.NewGuid(),
            OrderId = request.OrderId,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = decision.Approved ? PaymentStatuses.Captured : PaymentStatuses.Declined,
            CardHolderName = request.CardHolderName.Trim(),
            CardLast4 = card.Last4,
            CardBrand = card.Brand,
            DeclineCode = decision.DeclineCode,
            FailureReason = decision.Reason,
            RefundedAmount = 0m,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CorrelationId = correlationId,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        db.PaymentTransactions.Add(transaction);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null && ex.IsUniqueConstraintViolation)
        {
            // A concurrent retry carrying the same key committed first: return ITS outcome.
            db.ChangeTracker.Clear();
            var winner = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException($"Idempotency key '{idempotencyKey}' collided but no row was found.", ex);
            return Replay(winner, requestHash, idempotencyKey);
        }

        if (decision.Approved)
        {
            logger.LogInformation("Payment {PaymentId} CAPTURED: {Amount} {Currency} for order {OrderId} ({Brand} ****{Last4})",
                transaction.PaymentId, transaction.Amount, transaction.Currency, transaction.OrderId, card.Brand, card.Last4);
        }
        else
        {
            logger.LogWarning("Payment {PaymentId} DECLINED ({DeclineCode}): {Amount} {Currency} for order {OrderId} - {Reason}",
                transaction.PaymentId, transaction.DeclineCode, transaction.Amount, transaction.Currency,
                transaction.OrderId, transaction.FailureReason);
        }

        return new ChargeOutcome(decision.Approved ? ChargeStatus.Captured : ChargeStatus.Declined, transaction, Replayed: false);
    }

    public async Task<RefundOutcome> RefundAsync(
        Guid paymentId,
        RefundPaymentRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var payment = await db.PaymentTransactions
            .Include(p => p.PaymentRefunds)
            .SingleOrDefaultAsync(p => p.PaymentId == paymentId, cancellationToken);

        if (payment is null)
        {
            return new RefundOutcome(RefundStatus.NotFound, null, null);
        }

        if (payment.Status is not (PaymentStatuses.Captured or PaymentStatuses.PartiallyRefunded))
        {
            return new RefundOutcome(RefundStatus.NotRefundable, payment,
                $"Payment {paymentId} is {payment.Status}; only Captured or PartiallyRefunded payments can be refunded.");
        }

        var remaining = payment.Amount - payment.RefundedAmount;
        var amount = request.Amount ?? remaining;

        if (decimal.Round(amount, 2) != amount)
        {
            return new RefundOutcome(RefundStatus.InvalidAmount, payment, "Refund amount must have at most two decimal places.");
        }

        if (amount <= 0 || amount > remaining)
        {
            return new RefundOutcome(RefundStatus.InvalidAmount, payment,
                $"Refund amount {amount.ToString("F2", CultureInfo.InvariantCulture)} exceeds the refundable balance {remaining.ToString("F2", CultureInfo.InvariantCulture)} {payment.Currency}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // Explicit Add: RefundId is pre-generated while the column has a store default (NEWSEQUENTIALID()),
        // so adding through the navigation collection would make EF assume an EXISTING row and issue an UPDATE.
        db.PaymentRefunds.Add(new PaymentRefund
        {
            RefundId = SequentialGuid.NewGuid(),
            PaymentId = payment.PaymentId,
            Payment = payment,
            Amount = amount,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CorrelationId = correlationId,
            CreatedAtUtc = now
        });
        payment.RefundedAmount += amount;
        payment.Status = payment.RefundedAmount == payment.Amount ? PaymentStatuses.Refunded : PaymentStatuses.PartiallyRefunded;
        payment.UpdatedAtUtc = now;

        // RowVersion makes two concurrent refunds of the same payment fail fast (409) instead of over-refunding.
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Refunded {Amount} {Currency} of payment {PaymentId} (order {OrderId}); status {Status}",
            amount, payment.Currency, payment.PaymentId, payment.OrderId, payment.Status);

        return new RefundOutcome(RefundStatus.Refunded, payment, null);
    }

    private Task<PaymentTransaction?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.PaymentTransactions
            .Include(p => p.PaymentRefunds)
            .SingleOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken);

    private ChargeOutcome Replay(PaymentTransaction existing, string requestHash, string idempotencyKey)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            logger.LogWarning("Idempotency-Key {IdempotencyKey} was reused with a different payload (payment {PaymentId})",
                idempotencyKey, existing.PaymentId);
            return new ChargeOutcome(ChargeStatus.IdempotencyKeyReused, existing, Replayed: true);
        }

        logger.LogInformation(
            "IDEMPOTENT REPLAY for key {IdempotencyKey}: returning stored {Status} payment {PaymentId} without charging again",
            idempotencyKey, existing.Status, existing.PaymentId);

        var status = existing.Status == PaymentStatuses.Declined ? ChargeStatus.Declined : ChargeStatus.Captured;
        return new ChargeOutcome(status, existing, Replayed: true);
    }

    /// <summary>SHA-256 over the business fields (never over the full card number).</summary>
    private static string ComputeRequestHash(ChargePaymentRequest request, CardInfo card)
    {
        var canonical = string.Join('|',
            request.OrderId.ToString("N"),
            request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            request.Currency,
            card.Last4,
            request.CardHolderName.Trim(),
            request.ExpiryMonth.ToString(CultureInfo.InvariantCulture),
            request.ExpiryYear.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
