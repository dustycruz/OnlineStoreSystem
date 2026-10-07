// File: src/OnlineStoreSystem.InventoryService/Mapping/InventoryMappings.cs
using OnlineStoreSystem.InventoryService.Models;
using OnlineStoreSystem.InventoryService.Services;
using OnlineStoreSystem.Shared.DTOs;

namespace OnlineStoreSystem.InventoryService.Mapping;

public static class InventoryMappings
{
    public static InventoryItemDto ToDto(this InventoryItem item)
    {
        // Computed in memory (not read from the computed column) so the value is correct even for
        // tracked entities that were modified but not yet re-read from the database.
        var available = StockReservationService.Available(item);
        return new InventoryItemDto(
            item.InventoryItemId,
            item.ProductId,
            item.Sku,
            item.QuantityOnHand,
            item.QuantityReserved,
            available,
            item.ReorderThreshold,
            available <= item.ReorderThreshold,
            item.CreatedAtUtc,
            item.UpdatedAtUtc);
    }

    public static StockReservationDto ToDto(this StockReservation reservation) => new(
        reservation.StockReservationId,
        reservation.OrderId,
        reservation.ProductId,
        reservation.Quantity,
        reservation.Status,
        reservation.CreatedAtUtc,
        reservation.UpdatedAtUtc);
}
