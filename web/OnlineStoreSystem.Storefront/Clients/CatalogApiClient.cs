// File: web/OnlineStoreSystem.Storefront/Clients/CatalogApiClient.cs
using OnlineStoreSystem.Shared.Contracts.Catalog;
using OnlineStoreSystem.Shared.Contracts.Common;

namespace OnlineStoreSystem.Storefront.Clients;

/// <summary>Typed client for CatalogService (registered with IHttpClientFactory + resilience in Program.cs).</summary>
public sealed class CatalogApiClient(HttpClient httpClient, ILogger<CatalogApiClient> logger)
{
    private const string ApiName = "Catalog API";

    public Task<ApiResult<PagedResult<ProductDto>>> GetProductsAsync(
        string? search,
        string? category,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query.Add($"category={Uri.EscapeDataString(category.Trim())}");
        }

        var uri = $"api/v1/products?{string.Join('&', query)}";
        return ApiCall.ExecuteAsync<PagedResult<ProductDto>>(ApiName, token => httpClient.GetAsync(uri, token), logger,
            cancellationToken);
    }

    public Task<ApiResult<ProductDto>> GetProductAsync(int productId, CancellationToken cancellationToken) =>
        ApiCall.ExecuteAsync<ProductDto>(ApiName, token => httpClient.GetAsync($"api/v1/products/{productId}", token), logger,
            cancellationToken);
}
