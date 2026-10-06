// File: src/OnlineStoreSystem.PaymentService/Controllers/PaymentsController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.PaymentService.Data;
using OnlineStoreSystem.PaymentService.Mapping;
using OnlineStoreSystem.PaymentService.Services;
using OnlineStoreSystem.Shared.Contracts.Payments;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Problems;

namespace OnlineStoreSystem.PaymentService.Controllers;

/// <summary>
/// Charge and refund endpoints - implements <c>contracts/payment-v1.yaml</c>.
/// Deterministic failure triggers (saga compensation demo): amount <c>9999.00</c> or card
/// <c>4000000000000002</c> → <c>402 Payment Required</c> with RFC 7807 body (type
/// <c>urn:onlinestoresystem:problems:payment-declined</c>).
/// </summary>
[ApiController]
[Route("api/v1/payments")]
public sealed class PaymentsController(
    PaymentProcessor processor,
    PaymentDbContext db) : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    /// <summary>Charges a card for an order.</summary>
    [HttpPost("charge", Name = "ChargePayment")]
    [Consumes("application/json")]
    [ProducesResponseType<PaymentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status402PaymentRequired, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<ActionResult<PaymentDto>> Charge(
        [FromBody] ChargePaymentRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 100))
        {
            return this.ValidationProblemFor(new Dictionary<string, string[]>
            {
                [IdempotencyKeyHeader] = ["The Idempotency-Key header must contain 1 to 100 non-blank characters."]
            });
        }

        var outcome = await processor.ChargeAsync(request, idempotencyKey?.Trim(), HttpContext.CorrelationId, cancellationToken);
        var payment = outcome.Transaction;

        if (outcome.Replayed)
        {
            Response.Headers[IdempotentReplayedHeader] = "true";
        }

        return outcome.Status switch
        {
            ChargeStatus.Captured => CreatedAtRoute("GetPaymentById", new { paymentId = payment.PaymentId }, payment.ToDto()),

            ChargeStatus.Declined => this.ProblemWithExtensions(
                StatusCodes.Status402PaymentRequired,
                ProblemTypes.PaymentDeclined,
                "Payment declined",
                payment.FailureReason,
                new Dictionary<string, object?>
                {
                    ["paymentId"] = payment.PaymentId,
                    ["orderId"] = payment.OrderId,
                    ["declineCode"] = payment.DeclineCode,
                    ["amount"] = payment.Amount,
                    ["currency"] = payment.Currency
                }),

            _ => this.ProblemWithExtensions(
                StatusCodes.Status422UnprocessableEntity,
                ProblemTypes.IdempotencyKeyReused,
                "Idempotency-Key reused with a different request",
                $"The Idempotency-Key '{idempotencyKey}' was already used for a different charge request.",
                new Dictionary<string, object?> { ["paymentId"] = payment.PaymentId })
        };
    }

    /// <summary>Gets a payment transaction.</summary>
    [HttpGet("{paymentId:guid}", Name = "GetPaymentById")]
    [ProducesResponseType<PaymentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<PaymentDto>> GetPaymentById(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.PaymentRefunds)
            .SingleOrDefaultAsync(p => p.PaymentId == paymentId, cancellationToken);

        return payment is null ? this.NotFoundProblem("Payment", paymentId) : Ok(payment.ToDto());
    }

    /// <summary>Lists payments, optionally filtered by order (most recent first, max 100).</summary>
    [HttpGet(Name = "ListPayments")]
    [ProducesResponseType<IReadOnlyList<PaymentDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PaymentDto>>> GetPayments(
        [FromQuery] Guid? orderId,
        CancellationToken cancellationToken)
    {
        var query = db.PaymentTransactions.AsNoTracking().Include(p => p.PaymentRefunds).AsQueryable();
        if (orderId is { } id)
        {
            query = query.Where(p => p.OrderId == id);
        }

        var payments = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return Ok(payments.Select(p => p.ToDto()).ToList());
    }

    /// <summary>Refunds a captured payment fully (no amount) or partially.</summary>
    [HttpPost("{paymentId:guid}/refunds", Name = "RefundPayment")]
    [Consumes("application/json")]
    [ProducesResponseType<PaymentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<PaymentDto>> Refund(
        Guid paymentId,
        [FromBody] RefundPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await processor.RefundAsync(paymentId, request, HttpContext.CorrelationId, cancellationToken);

        return outcome.Status switch
        {
            RefundStatus.Refunded => Ok(outcome.Transaction!.ToDto()),
            RefundStatus.NotFound => this.NotFoundProblem("Payment", paymentId),
            RefundStatus.NotRefundable => this.ConflictProblem("Payment cannot be refunded", outcome.Detail!,
                ProblemTypes.InvalidStateTransition),
            _ => this.ValidationProblemFor(new Dictionary<string, string[]>
            {
                [nameof(RefundPaymentRequest.Amount)] = [outcome.Detail!]
            })
        };
    }
}
