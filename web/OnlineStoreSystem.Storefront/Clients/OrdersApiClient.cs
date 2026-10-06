// File: web/OnlineStoreSystem.Storefront/Clients/OrdersApiClient.cs
using OnlineStoreSystem.Shared.Contracts.Common;
using OnlineStoreSystem.Shared.Contracts.Orders;
using OnlineStoreSystem.Shared.Json;

namespace OnlineStoreSystem.Storefront.Clients;

/// <summary>
/// Typed client for OrderService. Its resilience pipeline does NOT retry POSTs (see Program.cs): a checkout
/// is not idempotent, so retrying after an ambiguous timeout could create a second order.
/// </summary>
public sealed class OrdersApiClient(HttpClient httpClient, ILogger<OrdersApiClient> logger)
{
    private const string ApiName = "Order API";

    /// <summary>Starts the checkout saga; success means 202 Accepted with the Pending order.</summary>
    public Task<ApiResult<OrderDto>> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Submitting checkout for customer {CustomerId} with {Lines} line(s)",
            request.CustomerId, request.Items.Count);
        return ApiCall.ExecuteAsync<OrderDto>(ApiName,
            token => httpClient.PostAsJsonAsync("api/v1/orders/checkout", request, JsonDefaults.Web, token),
            logger, cancellationToken);
    }

    public Task<ApiResult<OrderDto>> GetOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        ApiCall.ExecuteAsync<OrderDto>(ApiName, token => httpClient.GetAsync($"api/v1/orders/{orderId}", token), logger,
            cancellationToken);

    public Task<ApiResult<PagedResult<OrderSummaryDto>>> GetOrdersAsync(string customerId, CancellationToken cancellationToken) =>
        ApiCall.ExecuteAsync<PagedResult<OrderSummaryDto>>(ApiName,
            token => httpClient.GetAsync($"api/v1/orders?customerId={Uri.EscapeDataString(customerId)}&pageSize=50", token),
            logger, cancellationToken);
}
