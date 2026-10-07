// File: src/OnlineStoreSystem.OrderService/Mapping/OrderMappings.cs
using OnlineStoreSystem.OrderService.Models;
using OnlineStoreSystem.Shared.DTOs;

namespace OnlineStoreSystem.OrderService.Mapping;

public static class OrderMappings
{
    public static OrderDto ToDto(this Order order) => new(
        order.OrderId,
        order.OrderNumber,
        order.CustomerId,
        order.CustomerEmail,
        order.Status,
        order.TotalAmount,
        order.Currency,
        order.FailureReason,
        order.PaymentId,
        order.CorrelationId,
        order.CreatedAtUtc,
        order.UpdatedAtUtc,
        order.OrderItems
            .OrderBy(item => item.ProductId)
            .Select(item => item.ToDto())
            .ToList(),
        order.SagaState?.ToDto());

    public static OrderItemDto ToDto(this OrderItem item) => new(
        item.ProductId,
        item.Sku,
        item.ProductName,
        item.UnitPrice,
        item.Quantity,
        item.UnitPrice * item.Quantity);

    public static SagaDto ToDto(this SagaState saga) => new(
        saga.SagaId,
        saga.Status,
        saga.CurrentStep,
        saga.StockReserved,
        saga.PaymentProcessed,
        saga.PaymentId,
        saga.PaymentCardLast4,
        saga.RetryCount,
        saga.LastError,
        saga.StartedAtUtc,
        saga.UpdatedAtUtc,
        saga.CompletedAtUtc,
        saga.SagaStepLogs
            .OrderBy(step => step.OccurredAtUtc)
            .ThenBy(step => step.SagaStepLogId)
            .Select(step => new SagaStepDto(step.StepName, step.Outcome, step.Detail, step.OccurredAtUtc))
            .ToList());
}
