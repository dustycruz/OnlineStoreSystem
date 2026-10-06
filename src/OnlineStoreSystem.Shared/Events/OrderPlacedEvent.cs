// File: src/OnlineStoreSystem.Shared/Events/OrderPlacedEvent.cs
namespace OnlineStoreSystem.Shared.Events;

/// <summary>
/// Saga step "Reserve Stock" (asynchronous). Published by OrderService (via its transactional
/// outbox) after the order was persisted as <c>Pending</c>; consumed by InventoryService's
/// <c>OrderPlacedConsumer</c>, which replies with <see cref="StockReservedEvent"/> or
/// <see cref="StockReservationFailedEvent"/>.
/// Routing key: <c>order.placed</c>.
/// </summary>
public sealed record OrderPlacedEvent : IntegrationEvent
{
    public required Guid OrderId { get; init; }

    public required string OrderNumber { get; init; }

    public required string CustomerId { get; init; }

    public required decimal TotalAmount { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyList<OrderLineItem> Items { get; init; }
}
