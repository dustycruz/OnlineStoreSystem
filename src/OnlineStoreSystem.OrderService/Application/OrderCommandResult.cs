// File: src/OnlineStoreSystem.OrderService/Application/OrderCommandResult.cs
using OnlineStoreSystem.OrderService.Models;

namespace OnlineStoreSystem.OrderService.Application;

public enum OrderCommandStatus
{
    /// <summary>Synchronous command completed (create / update / delete draft).</summary>
    Success,

    /// <summary>Saga started; the outcome arrives asynchronously (HTTP 202).</summary>
    Accepted,

    ValidationFailed,
    NotFound,
    Conflict
}

/// <summary>Result of an order command, translated to HTTP by <c>OrdersController</c>.</summary>
public sealed record OrderCommandResult(
    OrderCommandStatus Status,
    Order? Order = null,
    IDictionary<string, string[]>? Errors = null,
    string? Detail = null)
{
    public static OrderCommandResult Success(Order order) => new(OrderCommandStatus.Success, order);

    public static OrderCommandResult Accepted(Order order) => new(OrderCommandStatus.Accepted, order);

    public static OrderCommandResult Invalid(IDictionary<string, string[]> errors) =>
        new(OrderCommandStatus.ValidationFailed, Errors: errors);

    public static OrderCommandResult Missing(Guid orderId) =>
        new(OrderCommandStatus.NotFound, Detail: $"Order '{orderId}' does not exist.");

    public static OrderCommandResult Conflicting(string detail) => new(OrderCommandStatus.Conflict, Detail: detail);
}
