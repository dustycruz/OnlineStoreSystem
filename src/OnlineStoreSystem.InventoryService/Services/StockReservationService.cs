// File: src/OnlineStoreSystem.InventoryService/Services/StockReservationService.cs
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.InventoryService.Data;
using OnlineStoreSystem.InventoryService.Models;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Shared.Events;

namespace OnlineStoreSystem.InventoryService.Services;

public enum ReserveStockStatus
{
    /// <summary>All lines were reserved by this call.</summary>
    Reserved,

    /// <summary>The order already holds reservations (idempotent repeat); nothing changed.</summary>
    AlreadyReserved,

    /// <summary>At least one line could not be satisfied; nothing changed.</summary>
    InsufficientStock
}

public sealed record ReserveStockResult(
    ReserveStockStatus Status,
    IReadOnlyList<StockReservation> Reservations,
    IReadOnlyList<ReservedStockLine> Lines,
    IReadOnlyList<StockShortage> Shortages);

/// <summary>
/// Stock reservation domain logic shared by the RabbitMQ consumers and the manual HTTP endpoints.
/// Methods only stage changes on the <see cref="InventoryDbContext"/>; the CALLER owns the transaction
/// and calls <c>SaveChangesAsync</c> (so the idempotency ledger entry commits atomically with the change).
/// Concurrent updates of the same stock row are detected by the <c>RowVersion</c> concurrency token and
/// the CHECK constraints on InventoryItems guarantee stock can never go negative.
/// </summary>
public sealed class StockReservationService(
    InventoryDbContext db,
    TimeProvider clock,
    ILogger<StockReservationService> logger)
{
    /// <summary>Reserves every line or nothing (all-or-nothing).</summary>
    public async Task<ReserveStockResult> ReserveAsync(
        Guid orderId,
        IEnumerable<(int ProductId, int Quantity)> lines,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var requested = lines
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));

        var existing = await db.StockReservations
            .Include(r => r.InventoryItem)
            .Where(r => r.OrderId == orderId)
            .ToListAsync(cancellationToken);

        if (existing.Count > 0)
        {
            logger.LogInformation("Order {OrderId} already holds {Count} reservation(s); reserve is a no-op",
                orderId, existing.Count);
            return new ReserveStockResult(ReserveStockStatus.AlreadyReserved, existing, ToLines(existing), []);
        }

        var productIds = requested.Keys.ToArray();
        var items = await db.InventoryItems
            .Where(i => productIds.Contains(i.ProductId))
            .ToDictionaryAsync(i => i.ProductId, cancellationToken);

        var shortages = new List<StockShortage>();
        foreach (var (productId, quantity) in requested.OrderBy(pair => pair.Key))
        {
            var available = items.TryGetValue(productId, out var item) ? Available(item) : 0;
            if (available < quantity)
            {
                shortages.Add(new StockShortage(productId, quantity, available));
            }
        }

        if (shortages.Count > 0)
        {
            logger.LogWarning("Insufficient stock for order {OrderId}: {Shortages}", orderId, Describe(shortages));
            return new ReserveStockResult(ReserveStockStatus.InsufficientStock, [], [], shortages);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var reservations = new List<StockReservation>(requested.Count);
        foreach (var (productId, quantity) in requested.OrderBy(pair => pair.Key))
        {
            var item = items[productId];
            item.QuantityReserved += quantity;
            item.UpdatedAtUtc = now;

            var reservation = new StockReservation
            {
                OrderId = orderId,
                InventoryItemId = item.InventoryItemId,
                InventoryItem = item,
                ProductId = productId,
                Quantity = quantity,
                Status = ReservationStatuses.Reserved,
                CorrelationId = correlationId,
                CreatedAtUtc = now
            };
            db.StockReservations.Add(reservation);
            reservations.Add(reservation);

            logger.LogInformation(
                "Reserved {Quantity} x product {ProductId} for order {OrderId}; on hand {OnHand}, reserved {Reserved}, available {Available}",
                quantity, productId, orderId, item.QuantityOnHand, item.QuantityReserved, Available(item));
        }

        return new ReserveStockResult(ReserveStockStatus.Reserved, reservations, ToLines(reservations), []);
    }

    /// <summary>
    /// COMPENSATION: returns every still-reserved line of the order to available stock.
    /// Idempotent: reservations already Released / Committed are left untouched.
    /// </summary>
    public async Task<IReadOnlyList<StockReservation>> ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var reservations = await db.StockReservations
            .Include(r => r.InventoryItem)
            .Where(r => r.OrderId == orderId && r.Status == ReservationStatuses.Reserved)
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var reservation in reservations)
        {
            var item = reservation.InventoryItem;
            item.QuantityReserved -= reservation.Quantity;
            item.UpdatedAtUtc = now;
            reservation.Status = ReservationStatuses.Released;
            reservation.UpdatedAtUtc = now;

            logger.LogInformation(
                "Released {Quantity} x product {ProductId} for order {OrderId}; available again {Available}",
                reservation.Quantity, reservation.ProductId, orderId, Available(item));
        }

        if (reservations.Count == 0)
        {
            logger.LogInformation("Order {OrderId} has no active reservation to release", orderId);
        }

        return reservations;
    }

    /// <summary>
    /// Payment succeeded: the reserved quantity physically leaves the warehouse
    /// (OnHand -= qty, Reserved -= qty). Idempotent like <see cref="ReleaseAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<StockReservation>> CommitAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var reservations = await db.StockReservations
            .Include(r => r.InventoryItem)
            .Where(r => r.OrderId == orderId && r.Status == ReservationStatuses.Reserved)
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var reservation in reservations)
        {
            var item = reservation.InventoryItem;
            item.QuantityOnHand -= reservation.Quantity;
            item.QuantityReserved -= reservation.Quantity;
            item.UpdatedAtUtc = now;
            reservation.Status = ReservationStatuses.Committed;
            reservation.UpdatedAtUtc = now;

            logger.LogInformation(
                "Committed {Quantity} x product {ProductId} for order {OrderId}; on hand now {OnHand}",
                reservation.Quantity, reservation.ProductId, orderId, item.QuantityOnHand);
        }

        return reservations;
    }

    public Task<List<StockReservation>> GetReservationsAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.StockReservations
            .AsNoTracking()
            .Include(r => r.InventoryItem)
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.ProductId)
            .ToListAsync(cancellationToken);

    /// <summary>Recomputes current shortages for a set of lines (used when replaying a failed decision).</summary>
    public async Task<IReadOnlyList<StockShortage>> FindShortagesAsync(
        IEnumerable<(int ProductId, int Quantity)> lines, CancellationToken cancellationToken)
    {
        var requested = lines
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        var productIds = requested.Keys.ToArray();
        var items = await db.InventoryItems.AsNoTracking()
            .Where(i => productIds.Contains(i.ProductId))
            .ToDictionaryAsync(i => i.ProductId, cancellationToken);

        return requested
            .OrderBy(pair => pair.Key)
            .Select(pair => new StockShortage(
                pair.Key,
                pair.Value,
                items.TryGetValue(pair.Key, out var item) ? Available(item) : 0))
            .Where(shortage => shortage.Available < shortage.Requested)
            .ToList();
    }

    public static int Available(InventoryItem item) => item.QuantityOnHand - item.QuantityReserved;

    public static string Describe(IEnumerable<StockShortage> shortages) =>
        string.Join("; ", shortages.Select(s =>
            $"product {s.ProductId}: requested {s.Requested}, available {s.Available}"));

    private static List<ReservedStockLine> ToLines(IEnumerable<StockReservation> reservations) =>
        reservations
            .OrderBy(r => r.ProductId)
            .Select(r => new ReservedStockLine(r.ProductId, r.Quantity, Available(r.InventoryItem)))
            .ToList();
}
