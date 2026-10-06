// File: src/OnlineStoreSystem.Shared/Contracts/Inventory/InventoryContracts.cs
using System.ComponentModel.DataAnnotations;
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.Shared.Contracts.Inventory;

/// <summary>Values of <c>InventoryDb.StockReservations.Status</c>.</summary>
public static class ReservationStatuses
{
    public const string Reserved = "Reserved";
    public const string Committed = "Committed";
    public const string Released = "Released";
}

/// <summary>Stock level of one product (schema <c>InventoryItem</c> in inventory-v1.yaml).</summary>
public sealed record InventoryItemDto(
    int InventoryItemId,
    int ProductId,
    string Sku,
    int QuantityOnHand,
    int QuantityReserved,
    int QuantityAvailable,
    int ReorderThreshold,
    bool NeedsReorder,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

/// <summary>Body of <c>POST /api/v1/inventory</c>.</summary>
public sealed record CreateInventoryItemRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.Sku, ErrorMessage = "Sku may contain only letters, digits and dashes.")]
    public string Sku { get; init; } = string.Empty;

    [Range(0, 1_000_000)]
    public int QuantityOnHand { get; init; }

    [Range(0, 1_000_000)]
    public int ReorderThreshold { get; init; } = 5;
}

/// <summary>Body of <c>PUT /api/v1/inventory/{productId}</c> (sets absolute stock levels).</summary>
public sealed record UpdateInventoryItemRequest
{
    [Range(0, 1_000_000)]
    public int QuantityOnHand { get; init; }

    [Range(0, 1_000_000)]
    public int ReorderThreshold { get; init; }
}

/// <summary>One line of a manual reservation request.</summary>
public sealed record StockLineRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; init; }

    [Range(1, 1000)]
    public int Quantity { get; init; }
}

/// <summary>Body of <c>POST /api/v1/inventory/reservations</c> (manual / admin reservation).</summary>
public sealed record ReserveStockRequest
{
    [NotEmptyGuid]
    public Guid OrderId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(50)]
    public List<StockLineRequest> Items { get; init; } = [];
}

/// <summary>A reservation row (schema <c>StockReservation</c>).</summary>
public sealed record StockReservationDto(
    long StockReservationId,
    Guid OrderId,
    int ProductId,
    int Quantity,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

/// <summary>Result of reserve / release / commit operations (schema <c>ReservationResult</c>).</summary>
public sealed record ReservationResultDto(
    Guid OrderId,
    string Outcome,
    IReadOnlyList<StockReservationDto> Reservations);

/// <summary>Insufficient stock details used in 409 problem responses.</summary>
public sealed record StockShortageDto(int ProductId, int Requested, int Available);
