// File: src/OnlineStoreSystem.OrderService/Clients/CatalogClient.cs
using System.Net;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Shared.Http;
using OnlineStoreSystem.Shared.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OnlineStoreSystem.OrderService.Clients;

/// <summary>
/// Typed HTTP client for CatalogService (Module 3 - synchronous integration).
/// Created by <c>IHttpClientFactory</c>; its handler pipeline is:
/// <c>CorrelationIdDelegatingHandler → standard resilience handler (retry/backoff/jitter/timeouts/circuit breaker) → HTTP</c>
/// (see Program.cs). When the pipeline gives up, a <see cref="DependencyUnavailableException"/> is thrown and
/// surfaces to API callers as a 503 RFC 7807 problem.
/// </summary>
public sealed class CatalogClient(HttpClient httpClient, ILogger<CatalogClient> logger)
{
    public const string DependencyName = "CatalogService";

    /// <summary>Batch lookup of products by id (inactive products included so they can be reported).</summary>
    public async Task<IReadOnlyDictionary<int, ProductDto>> GetProductsAsync(
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<int, ProductDto>();
        }

        var ids = productIds.Distinct().OrderBy(id => id).ToArray();
        var requestUri = $"api/v1/products?{string.Join('&', ids.Select(id => $"ids={id}"))}&includeInactive=true&pageSize=100";

        logger.LogInformation("Validating product(s) {ProductIds} against {Dependency}: GET {RequestUri}",
            string.Join(',', ids), DependencyName, requestUri);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(requestUri, cancellationToken);
        }
        catch (Exception ex) when (IsTransientFailure(ex, cancellationToken))
        {
            logger.LogError("{Dependency} unreachable after all retries: {Error}", DependencyName, ex.Message);
            throw new DependencyUnavailableException(DependencyName,
                $"{DependencyName} could not be reached ({ex.GetType().Name}: {ex.Message}).", ex);
        }

        using (response)
        {
            if (IsTransientStatus(response.StatusCode))
            {
                logger.LogError("{Dependency} still answered {StatusCode} after all retries", DependencyName, (int)response.StatusCode);
                throw new DependencyUnavailableException(DependencyName,
                    $"{DependencyName} answered {(int)response.StatusCode} {response.ReasonPhrase} after all retries.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.ReadProblemAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"{DependencyName} rejected the product lookup with {(int)response.StatusCode}: {problem?.Detail ?? problem?.Title}");
            }

            var page = await response.Content.ReadFromJsonAsync<PagedResult<ProductDto>>(JsonDefaults.Web, cancellationToken)
                ?? throw new InvalidOperationException($"{DependencyName} returned an empty body.");

            logger.LogInformation("{Dependency} returned {Count} of {Requested} requested product(s)",
                DependencyName, page.Items.Count, ids.Length);

            return page.Items.ToDictionary(p => p.ProductId);
        }
    }

    internal static bool IsTransientFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        HttpRequestException => true,
        TimeoutRejectedException => true,
        BrokenCircuitException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false
    };

    internal static bool IsTransientStatus(HttpStatusCode statusCode) =>
        (int)statusCode >= 500 || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}
