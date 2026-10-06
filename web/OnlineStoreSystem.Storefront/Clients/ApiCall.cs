// File: web/OnlineStoreSystem.Storefront/Clients/ApiCall.cs
using System.Net;
using OnlineStoreSystem.Shared.Http;
using OnlineStoreSystem.Shared.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OnlineStoreSystem.Storefront.Clients;

/// <summary>Shared response handling for the Storefront's typed API clients.</summary>
internal static class ApiCall
{
    public static async Task<ApiResult<T>> ExecuteAsync<T>(
        string apiName,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await send(cancellationToken);
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
