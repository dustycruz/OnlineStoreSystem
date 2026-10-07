// File: src/OnlineStoreSystem.OrderService/Controllers/OrdersController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.OrderService.Application;
using OnlineStoreSystem.OrderService.Data;
using OnlineStoreSystem.OrderService.Mapping;
using OnlineStoreSystem.OrderService.Models;
using OnlineStoreSystem.OrderService.Saga;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Shared.Problems;

namespace OnlineStoreSystem.OrderService.Controllers;

/// <summary>
/// Orders CRUD + checkout - implements <c>contracts/orders-v1.yaml</c>.
/// <list type="bullet">
///   <item><c>POST /api/v1/orders/checkout</c> - one-shot checkout: starts the saga, answers <b>202 Accepted</b>
///         with <c>Location</c>; poll <c>GET /api/v1/orders/{id}</c> until the status is Completed or Cancelled.</item>
///   <item><c>POST/PUT/DELETE /api/v1/orders</c> - manage DRAFT orders; <c>POST /api/v1/orders/{id}/checkout</c> submits one.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/orders")]
public sealed class OrdersController(
    OrderDbContext db,
    OrderCommandService commands,
    OrderSagaOrchestrator orchestrator) : ControllerBase
{
    private const int MaxPageSize = 100;

    private static readonly HashSet<string> KnownStatuses =
    [
        OrderStatuses.Draft, OrderStatuses.Pending, OrderStatuses.StockReserved, OrderStatuses.Completed, OrderStatuses.Cancelled
    ];

    /// <summary>Lists orders (newest first), optionally filtered by customer and status.</summary>
    [HttpGet(Name = "ListOrders")]
    [ProducesResponseType<PagedResult<OrderSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<PagedResult<OrderSummaryDto>>> GetOrders(
        [FromQuery] string? customerId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be greater than or equal to 1."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (status is not null && !KnownStatuses.Contains(status))
        {
            errors["status"] = [$"status must be one of: {string.Join(", ", KnownStatuses)}."];
        }

        if (errors.Count > 0)
        {
            return this.ValidationProblemFor(errors);
        }

        var query = db.Orders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(customerId))
        {
            var customer = customerId.Trim();
            query = query.Where(o => o.CustomerId == customer);
        }

        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrderSummaryDto(
                o.OrderId,
                o.OrderNumber,
                o.CustomerId,
                o.Status,
                o.TotalAmount,
                o.Currency,
                o.OrderItems.Count,
                o.FailureReason,
                o.CreatedAtUtc,
                o.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return Ok(new PagedResult<OrderSummaryDto>(items, page, pageSize, totalCount));
    }

    /// <summary>Gets an order with its items and the saga state / audit trail.</summary>
    [HttpGet("{orderId:guid}", Name = "GetOrderById")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<OrderDto>> GetOrderById(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        return order is null ? this.NotFoundProblem("Order", orderId) : Ok(order.ToDto());
    }

    /// <summary>Gets only the saga of an order.</summary>
    [HttpGet("{orderId:guid}/saga", Name = "GetOrderSaga")]
    [ProducesResponseType<SagaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<SagaDto>> GetOrderSaga(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        if (order is null)
        {
            return this.NotFoundProblem("Order", orderId);
        }

        return order.SagaState is null
            ? this.NotFoundProblem("Saga of order", orderId)
            : Ok(order.SagaState.ToDto());
    }

    /// <summary>Creates a DRAFT order (products validated and priced by CatalogService).</summary>
    [HttpPost(Name = "CreateOrder")]
    [Consumes("application/json")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<OrderDto>> CreateOrder(
        [FromBody] CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await commands.CreateDraftAsync(request, cancellationToken);
        return Translate(result, Guid.Empty,
            order => CreatedAtRoute("GetOrderById", new { orderId = order.OrderId }, order.ToDto()));
    }

    /// <summary>Replaces a DRAFT order.</summary>
    [HttpPut("{orderId:guid}", Name = "UpdateOrder")]
    [Consumes("application/json")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<OrderDto>> UpdateOrder(
        Guid orderId,
        [FromBody] UpdateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await commands.UpdateDraftAsync(orderId, request, cancellationToken);
        return Translate(result, orderId, order => Ok(order.ToDto()));
    }

    /// <summary>Deletes a DRAFT order, or a CANCELLED order whose saga has finished.</summary>
    [HttpDelete("{orderId:guid}", Name = "DeleteOrder")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> DeleteOrder(Guid orderId, CancellationToken cancellationToken)
    {
        var result = await commands.DeleteAsync(orderId, cancellationToken);
        return Translate(result, orderId, _ => NoContent());
    }

    /// <summary>
    /// One-shot checkout: validates products (sync, resilient), creates the order as Pending and starts the
    /// orchestrated saga. Returns 202 Accepted; the final status arrives asynchronously.
    /// </summary>
    [HttpPost("checkout", Name = "Checkout")]
    [Consumes("application/json")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<OrderDto>> Checkout(
        [FromBody] CheckoutRequest request,
        CancellationToken cancellationToken)
    {
        var result = await orchestrator.StartCheckoutAsync(request, cancellationToken);
        return Translate(result, Guid.Empty,
            order => AcceptedAtRoute("GetOrderById", new { orderId = order.OrderId }, order.ToDto()));
    }

    /// <summary>Checks out an existing DRAFT order (starts the saga). Returns 202 Accepted.</summary>
    [HttpPost("{orderId:guid}/checkout", Name = "CheckoutDraft")]
    [Consumes("application/json")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<OrderDto>> CheckoutDraft(
        Guid orderId,
        [FromBody] SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await orchestrator.SubmitDraftAsync(orderId, request, cancellationToken);
        return Translate(result, orderId,
            order => AcceptedAtRoute("GetOrderById", new { orderId = order.OrderId }, order.ToDto()));
    }

    private Task<Order?> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .Include(o => o.SagaState).ThenInclude(s => s!.SagaStepLogs)
            .SingleOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

    private ActionResult Translate(OrderCommandResult result, Guid orderId, Func<Order, ActionResult> onSuccess) =>
        result.Status switch
        {
            OrderCommandStatus.Success or OrderCommandStatus.Accepted => onSuccess(result.Order!),
            OrderCommandStatus.ValidationFailed => this.ValidationProblemFor(result.Errors!,
                "One or more order lines were rejected by the product validation step."),
            OrderCommandStatus.NotFound => this.NotFoundProblem("Order", orderId),
            _ => this.ConflictProblem("Invalid order state", result.Detail!, ProblemTypes.InvalidStateTransition)
        };
}
