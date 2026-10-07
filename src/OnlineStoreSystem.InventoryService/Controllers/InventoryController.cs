// File: src/OnlineStoreSystem.InventoryService/Controllers/InventoryController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.InventoryService.Data;
using OnlineStoreSystem.InventoryService.Mapping;
using OnlineStoreSystem.InventoryService.Models;
using OnlineStoreSystem.InventoryService.Services;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Shared.Problems;

namespace OnlineStoreSystem.InventoryService.Controllers;

/// <summary>
/// Stock levels CRUD + manual reserve / release / commit endpoints - implements
/// <c>contracts/inventory-v1.yaml</c>. The saga normally drives reservations through RabbitMQ;
/// these endpoints exist for operations staff and for demos.
/// </summary>
[ApiController]
[Route("api/v1/inventory")]
public sealed class InventoryController(
    InventoryDbContext db,
    StockReservationService reservations,
    TimeProvider clock,
    ILogger<InventoryController> logger) : ControllerBase
{
    /// <summary>Lists stock levels for all products.</summary>
    [HttpGet(Name = "ListInventory")]
    [ProducesResponseType<IReadOnlyList<InventoryItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<InventoryItemDto>>> GetInventory(
        [FromQuery] bool lowStockOnly = false,
        CancellationToken cancellationToken = default)
    {
        var items = await db.InventoryItems
            .AsNoTracking()
            .OrderBy(i => i.ProductId)
            .ToListAsync(cancellationToken);

        var result = items
            .Select(i => i.ToDto())
            .Where(dto => !lowStockOnly || dto.NeedsReorder)
            .ToList();

        return Ok(result);
    }

    /// <summary>Gets the stock level of one product.</summary>
    [HttpGet("{productId:int}", Name = "GetInventoryItem")]
    [ProducesResponseType<InventoryItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<InventoryItemDto>> GetInventoryItem(int productId, CancellationToken cancellationToken)
    {
        var item = await db.InventoryItems.AsNoTracking()
            .SingleOrDefaultAsync(i => i.ProductId == productId, cancellationToken);

        return item is null ? this.NotFoundProblem("Inventory item for product", productId) : Ok(item.ToDto());
    }

    /// <summary>Creates the stock row for a product.</summary>
    [HttpPost(Name = "CreateInventoryItem")]
    [Consumes("application/json")]
    [ProducesResponseType<InventoryItemDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<InventoryItemDto>> CreateInventoryItem(
        [FromBody] CreateInventoryItemRequest request,
        CancellationToken cancellationToken)
    {
        var sku = request.Sku.Trim();
        if (await db.InventoryItems.AnyAsync(i => i.ProductId == request.ProductId || i.Sku == sku, cancellationToken))
        {
            return this.ConflictProblem("Inventory item already exists",
                $"Product {request.ProductId} (or SKU '{sku}') already has a stock record.");
        }

        var item = new InventoryItem
        {
            ProductId = request.ProductId,
            Sku = sku,
            QuantityOnHand = request.QuantityOnHand,
            QuantityReserved = 0,
            ReorderThreshold = request.ReorderThreshold,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        db.InventoryItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation)
        {
            return this.ConflictProblem("Inventory item already exists",
                $"Product {request.ProductId} (or SKU '{sku}') already has a stock record.");
        }

        logger.LogInformation("Created inventory item for product {ProductId} with {Quantity} on hand",
            item.ProductId, item.QuantityOnHand);
        return CreatedAtRoute("GetInventoryItem", new { productId = item.ProductId }, item.ToDto());
    }

    /// <summary>Sets absolute stock levels (e.g. after a stock take).</summary>
    [HttpPut("{productId:int}", Name = "UpdateInventoryItem")]
    [Consumes("application/json")]
    [ProducesResponseType<InventoryItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<InventoryItemDto>> UpdateInventoryItem(
        int productId,
        [FromBody] UpdateInventoryItemRequest request,
        CancellationToken cancellationToken)
    {
        var item = await db.InventoryItems.SingleOrDefaultAsync(i => i.ProductId == productId, cancellationToken);
        if (item is null)
        {
            return this.NotFoundProblem("Inventory item for product", productId);
        }

        if (request.QuantityOnHand < item.QuantityReserved)
        {
            return this.ConflictProblem("Quantity below reservations",
                $"QuantityOnHand ({request.QuantityOnHand}) cannot be lower than the currently reserved quantity ({item.QuantityReserved}).",
                ProblemTypes.InvalidStateTransition);
        }

        item.QuantityOnHand = request.QuantityOnHand;
        item.ReorderThreshold = request.ReorderThreshold;
        item.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Stock of product {ProductId} set to {OnHand} on hand", productId, item.QuantityOnHand);
        return Ok(item.ToDto());
    }

    /// <summary>Deletes the stock row of a product (refused while reservations are active).</summary>
    [HttpDelete("{productId:int}", Name = "DeleteInventoryItem")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> DeleteInventoryItem(int productId, CancellationToken cancellationToken)
    {
        var item = await db.InventoryItems.SingleOrDefaultAsync(i => i.ProductId == productId, cancellationToken);
        if (item is null)
        {
            return this.NotFoundProblem("Inventory item for product", productId);
        }

        var hasActiveReservations = await db.StockReservations.AnyAsync(
            r => r.InventoryItemId == item.InventoryItemId && r.Status == ReservationStatuses.Reserved, cancellationToken);
        if (hasActiveReservations)
        {
            return this.ConflictProblem("Stock is reserved",
                $"Product {productId} has active reservations; release or commit them first.",
                ProblemTypes.InvalidStateTransition);
        }

        db.InventoryItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Deleted inventory item of product {ProductId}", productId);
        return NoContent();
    }

    /// <summary>Lists the reservations of an order.</summary>
    [HttpGet("reservations/{orderId:guid}", Name = "GetReservations")]
    [ProducesResponseType<ReservationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<ReservationResultDto>> GetReservations(Guid orderId, CancellationToken cancellationToken)
    {
        var existing = await reservations.GetReservationsAsync(orderId, cancellationToken);
        if (existing.Count == 0)
        {
            return this.NotFoundProblem("Reservations of order", orderId);
        }

        return Ok(new ReservationResultDto(orderId, "Found", existing.Select(r => r.ToDto()).ToList()));
    }

    /// <summary>Manually reserves stock for an order (all lines or nothing).</summary>
    [HttpPost("reservations", Name = "ReserveStock")]
    [Consumes("application/json")]
    [ProducesResponseType<ReservationResultDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ReservationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<ReservationResultDto>> ReserveStock(
        [FromBody] ReserveStockRequest request,
        CancellationToken cancellationToken)
    {
        var result = await reservations.ReserveAsync(
            request.OrderId,
            request.Items.Select(i => (i.ProductId, i.Quantity)),
            HttpContext.CorrelationId,
            cancellationToken);

        switch (result.Status)
        {
            case ReserveStockStatus.InsufficientStock:
                return this.ProblemWithExtensions(
                    StatusCodes.Status409Conflict,
                    ProblemTypes.InsufficientStock,
                    "Insufficient stock",
                    StockReservationService.Describe(result.Shortages),
                    new Dictionary<string, object?>
                    {
                        ["orderId"] = request.OrderId,
                        ["shortages"] = result.Shortages
                            .Select(s => new StockShortageDto(s.ProductId, s.Requested, s.Available))
                            .ToList()
                    });

            case ReserveStockStatus.AlreadyReserved:
                return Ok(new ReservationResultDto(request.OrderId, "AlreadyReserved",
                    result.Reservations.Select(r => r.ToDto()).ToList()));

            default:
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Manually reserved stock for order {OrderId}", request.OrderId);
                return CreatedAtRoute("GetReservations", new { orderId = request.OrderId },
                    new ReservationResultDto(request.OrderId, "Reserved",
                        result.Reservations.Select(r => r.ToDto()).ToList()));
        }
    }

    /// <summary>Manually releases (compensates) the active reservations of an order. Idempotent.</summary>
    [HttpPost("reservations/{orderId:guid}/release", Name = "ReleaseStock")]
    [ProducesResponseType<ReservationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<ReservationResultDto>> ReleaseStock(Guid orderId, CancellationToken cancellationToken)
    {
        var released = await reservations.ReleaseAsync(orderId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Manually released {Count} reservation(s) of order {OrderId}", released.Count, orderId);
        return Ok(new ReservationResultDto(orderId, released.Count > 0 ? "Released" : "NothingToRelease",
            released.Select(r => r.ToDto()).ToList()));
    }

    /// <summary>Manually commits the active reservations of an order (stock leaves the warehouse). Idempotent.</summary>
    [HttpPost("reservations/{orderId:guid}/commit", Name = "CommitStock")]
    [ProducesResponseType<ReservationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<ReservationResultDto>> CommitStock(Guid orderId, CancellationToken cancellationToken)
    {
        var committed = await reservations.CommitAsync(orderId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Manually committed {Count} reservation(s) of order {OrderId}", committed.Count, orderId);
        return Ok(new ReservationResultDto(orderId, committed.Count > 0 ? "Committed" : "NothingToCommit",
            committed.Select(r => r.ToDto()).ToList()));
    }
}
