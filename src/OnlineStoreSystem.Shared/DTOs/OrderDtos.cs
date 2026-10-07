// File: src/OnlineStoreSystem.Shared/DTOs/OrderDtos.cs
namespace OnlineStoreSystem.Shared.DTOs;

/// <summary>Full order representation (schema <c>Order</c> in orders-v1.yaml).</summary>
public sealed record OrderDto(
    Guid OrderId,
    string OrderNumber,
    string CustomerId,
    string CustomerEmail,
    string Status,
    decimal TotalAmount,
    string Currency,
    string? FailureReason,
    Guid? PaymentId,
    string? CorrelationId,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    IReadOnlyList<OrderItemDto> Items,
    SagaDto? Saga);

/// <summary>Order line with the catalog price snapshot (schema <c>OrderItem</c>).</summary>
public sealed record OrderItemDto(
    int ProductId,
    string Sku,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

/// <summary>Lightweight row for order lists (schema <c>OrderSummary</c>).</summary>
public sealed record OrderSummaryDto(
    Guid OrderId,
    string OrderNumber,
    string CustomerId,
    string Status,
    decimal TotalAmount,
    string Currency,
    int ItemCount,
    string? FailureReason,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

/// <summary>State of the orchestrated checkout saga (schema <c>Saga</c>).</summary>
public sealed record SagaDto(
    Guid SagaId,
    string Status,
    string CurrentStep,
    bool StockReserved,
    bool PaymentProcessed,
    Guid? PaymentId,
    string? PaymentCardLast4,
    int RetryCount,
    string? LastError,
    DateTime StartedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<SagaStepDto> Steps);

/// <summary>One entry of the saga audit trail (schema <c>SagaStep</c>).</summary>
public sealed record SagaStepDto(
    string StepName,
    string Outcome,
    string? Detail,
    DateTime OccurredAtUtc);
