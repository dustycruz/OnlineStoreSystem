// File: web/OnlineStoreSystem.Storefront/Clients/InventoryApiClient.cs
using OnlineStoreSystem.Shared.Contracts.Inventory;

namespace OnlineStoreSystem.Storefront.Clients;

/// <summary>
/// Typed client for InventoryService, used only to DISPLAY availability. Failures degrade gracefully
/// (the catalog still renders with "stock unknown").
/// </summary>
public sealed class InventoryApiClient(HttpClient httpClient, ILogger<InventoryApiClient> logger)
{
    public async Task<IReadOnlyDictionary<int, InventoryItemDto>> GetStockLevelsAsync(CancellationToken cancellationToken)
    {
        var result = await ApiCall.ExecuteAsync<List<InventoryItemDto>>(
            "Inventory API", token => httpClient.GetAsync("api/v1/inventory", token), logger, cancellationToken);

        if (!result.IsSuccess)
        {
            logger.LogWarning("Stock levels unavailable: {Error}", result.ErrorMessage);
            return new Dictionary<int, InventoryItemDto>();
        }

        return result.Value!.ToDictionary(item => item.ProductId);
    }
}
