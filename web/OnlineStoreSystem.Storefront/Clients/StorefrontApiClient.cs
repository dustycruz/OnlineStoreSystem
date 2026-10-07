// File: web/OnlineStoreSystem.Storefront/Clients/StorefrontApiClient.cs
using System.Net;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Shared.Http;
using OnlineStoreSystem.Shared.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OnlineStoreSystem.Storefront.Clients;

/// <summary>
/// The Storefront's single gateway to the back-end APIs. It uses three NAMED clients from
/// <see cref="IHttpClientFactory"/> (registered in Program.cs), each with its own base address,
/// <c>X-Correlation-Id</c> propagation and resilience pipeline:
/// <list type="bullet">
/// <item><see cref="CatalogClientName"/> - CatalogService (port 5001)</item>
/// <item><see cref="OrderClientName"/> - OrderService (port 5002); never retries POST because checkout is not idempotent</item>
/// <item><see cref="InventoryClientName"/> - InventoryService (port 5003); display only, failures degrade gracefully</item>
/// </list>
/// Every call returns an <see cref="ApiResult{T}"/> instead of throwing, so pages can render RFC 7807 problems.
/// </summary>
public sealed class StorefrontApiClient(IHttpClientFactory httpClientFactory, ILogger<StorefrontApiClient> logger)
{
    public const string CatalogClientName = "CatalogApi";
    public const string OrderClientName = "OrderApi";
    public const string InventoryClientName = "InventoryApi";

    private const string CatalogApiName = "Catalog API";
    private const string OrderApiName = "Order API";
    private const string InventoryApiName = "Inventory API";

    // ------------------------------------------------------------------ catalog

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
        return ExecuteAsync<PagedResult<ProductDto>>(CatalogApiName, CatalogClientName,
            (client, token) => client.GetAsync(uri, token), cancellationToken);
    }

    public Task<ApiResult<ProductDto>> GetProductAsync(int productId, CancellationToken cancellationToken) =>
        ExecuteAsync<ProductDto>(CatalogApiName, CatalogClientName,
            (client, token) => client.GetAsync($"api/v1/products/{productId}", token), cancellationToken);

    // ---------------------------------------------------------------- inventory

    /// <summary>Stock levels keyed by product id; an empty dictionary when InventoryService is unavailable.</summary>
    public async Task<IReadOnlyDictionary<int, InventoryItemDto>> GetStockLevelsAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync<List<InventoryItemDto>>(InventoryApiName, InventoryClientName,
            (client, token) => client.GetAsync("api/v1/inventory", token), cancellationToken);

        if (!result.IsSuccess)
        {
            logger.LogWarning("Stock levels unavailable: {Error}", result.ErrorMessage);
            return new Dictionary<int, InventoryItemDto>();
        }

        return result.Value!.ToDictionary(item => item.ProductId);
    }

    // ------------------------------------------------------------------- orders

    /// <summary>Starts the checkout saga; success means 202 Accepted with the Pending order.</summary>
    public Task<ApiResult<OrderDto>> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Submitting checkout for customer {CustomerId} with {Lines} line(s)",
            request.CustomerId, request.Items.Count);
        return ExecuteAsync<OrderDto>(OrderApiName, OrderClientName,
            (client, token) => client.PostAsJsonAsync("api/v1/orders/checkout", request, JsonDefaults.Web, token),
            cancellationToken);
    }

    public Task<ApiResult<OrderDto>> GetOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        ExecuteAsync<OrderDto>(OrderApiName, OrderClientName,
            (client, token) => client.GetAsync($"api/v1/orders/{orderId}", token), cancellationToken);

    public Task<ApiResult<PagedResult<OrderSummaryDto>>> GetOrdersAsync(string customerId, CancellationToken cancellationToken) =>
        ExecuteAsync<PagedResult<OrderSummaryDto>>(OrderApiName, OrderClientName,
            (client, token) => client.GetAsync($"api/v1/orders?customerId={Uri.EscapeDataString(customerId)}&pageSize=50", token),
            cancellationToken);

    // --------------------------------------------------------- response handling

    private async Task<ApiResult<T>> ExecuteAsync<T>(
        string apiName,
        string clientName,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(clientName);

        HttpResponseMessage response;
        try
        {
            response = await send(client, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogError("{Api} unreachable after retries: {Error}", apiName, ex.Message);
            return ApiResult<T>.Failed(null, null,
                $"{apiName} is currently unreachable ({ex.GetType().Name}). Please try again in a moment.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Web, cancellationToken);
                return value is null
                    ? ApiResult<T>.Failed((int)response.StatusCode, null, $"{apiName} returned an empty response.")
                    : ApiResult<T>.Ok(value, (int)response.StatusCode);
            }

            var problem = await response.ReadProblemAsync(cancellationToken);
            var message = problem?.Detail ?? problem?.Title ?? $"{apiName} answered {(int)response.StatusCode} {response.ReasonPhrase}.";
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                logger.LogWarning("{Api} reported a dependency outage: {Message}", apiName, message);
            }

            return ApiResult<T>.Failed((int)response.StatusCode, problem, message);
        }
    }
}
