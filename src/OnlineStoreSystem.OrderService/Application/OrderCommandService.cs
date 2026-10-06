// File: src/OnlineStoreSystem.OrderService/Application/OrderCommandService.cs
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.OrderService.Data;
using OnlineStoreSystem.OrderService.Data.Entities;
using OnlineStoreSystem.Shared.Contracts.Orders;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Identifiers;

namespace OnlineStoreSystem.OrderService.Application;

/// <summary>
/// Plain CRUD on DRAFT orders (no saga involved). A draft becomes a real order through
/// <c>POST /api/v1/orders/{orderId}/checkout</c>, which hands it to the saga orchestrator.
/// </summary>
public sealed class OrderCommandService(
    OrderDbContext db,
    ProductValidator productValidator,
    ICorrelationIdAccessor correlationIdAccessor,
    TimeProvider clock,
    ILogger<OrderCommandService> logger)
{
    public async Task<OrderCommandResult> CreateDraftAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var validation = await productValidator.ValidateAsync(request.Items, cancellationToken);
        if (!validation.IsValid)
        {
            return OrderCommandResult.Invalid(validation.Errors);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            OrderId = SequentialGuid.NewGuid(),
            OrderNumber = OrderNumberGenerator.Next(now),
            CustomerId = request.CustomerId.Trim(),
            CustomerEmail = request.CustomerEmail.Trim(),
            Status = OrderStatuses.Draft,
            CorrelationId = correlationIdAccessor.CorrelationId,
            CreatedAtUtc = now
        };
        OrderLines.Apply(order, validation.Lines, validation.Currency);

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Draft order {OrderNumber} ({OrderId}) created: {Lines}",
            order.OrderNumber, order.OrderId, validation.Describe());
        return OrderCommandResult.Success(order);
    }

    public async Task<OrderCommandResult> UpdateDraftAsync(
        Guid orderId,
        UpdateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .Include(o => o.OrderItems)
            .SingleOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

        if (order is null)
        {
            return OrderCommandResult.Missing(orderId);
        }

        if (order.Status != OrderStatuses.Draft)
        {
            return OrderCommandResult.Conflicting(
                $"Order {order.OrderNumber} is {order.Status}; only Draft orders can be modified.");
        }

        var validation = await productValidator.ValidateAsync(request.Items, cancellationToken);
        if (!validation.IsValid)
        {
            return OrderCommandResult.Invalid(validation.Errors);
        }

        order.CustomerId = request.CustomerId.Trim();
        order.CustomerEmail = request.CustomerEmail.Trim();
        order.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        OrderLines.Apply(order, validation.Lines, validation.Currency);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Draft order {OrderNumber} updated: {Lines}", order.OrderNumber, validation.Describe());
        return OrderCommandResult.Success(order);
    }

    public async Task<OrderCommandResult> DeleteAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .Include(o => o.SagaState)
            .SingleOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

        if (order is null)
        {
            return OrderCommandResult.Missing(orderId);
        }

        var deletable = order.Status == OrderStatuses.Draft
                        || (order.Status == OrderStatuses.Cancelled
                            && (order.SagaState is null || SagaStatuses.IsTerminal(order.SagaState.Status)));
        if (!deletable)
        {
            return OrderCommandResult.Conflicting(
                $"Order {order.OrderNumber} is {order.Status} (saga {order.SagaState?.Status ?? "n/a"}); only Draft orders or " +
                "Cancelled orders whose saga has finished can be deleted.");
        }

        db.Orders.Remove(order); // cascades to OrderItems, SagaStates and SagaStepLogs (FK ON DELETE CASCADE)
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Order {OrderNumber} ({OrderId}) deleted", order.OrderNumber, orderId);
        return OrderCommandResult.Success(order);
    }
}
