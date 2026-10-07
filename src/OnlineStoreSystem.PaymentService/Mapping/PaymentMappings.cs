// File: src/OnlineStoreSystem.PaymentService/Mapping/PaymentMappings.cs
using OnlineStoreSystem.PaymentService.Models;
using OnlineStoreSystem.Shared.DTOs;

namespace OnlineStoreSystem.PaymentService.Mapping;

public static class PaymentMappings
{
    public static PaymentDto ToDto(this PaymentTransaction payment) => new(
        payment.PaymentId,
        payment.OrderId,
        payment.Amount,
        payment.Currency,
        payment.Status,
        payment.CardBrand,
        payment.CardLast4,
        payment.DeclineCode,
        payment.FailureReason,
        payment.RefundedAmount,
        payment.CreatedAtUtc,
        payment.UpdatedAtUtc,
        payment.PaymentRefunds
            .OrderBy(r => r.CreatedAtUtc)
            .Select(r => new RefundDto(r.RefundId, r.Amount, r.Reason, r.CreatedAtUtc))
            .ToList());
}
