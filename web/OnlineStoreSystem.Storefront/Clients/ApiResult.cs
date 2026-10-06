// File: web/OnlineStoreSystem.Storefront/Clients/ApiResult.cs
using Microsoft.AspNetCore.Http;

namespace OnlineStoreSystem.Storefront.Clients;

/// <summary>
/// Outcome of a back-end call as seen by the UI: either a value, or an RFC 7807 problem / transport error
/// that can be rendered to the shopper without crashing the page.
/// </summary>
public sealed record ApiResult<T>(
    T? Value,
    int? StatusCode,
    HttpValidationProblemDetails? Problem,
    string? ErrorMessage)
{
    public bool IsSuccess => Value is not null && ErrorMessage is null && Problem is null;

    public static ApiResult<T> Ok(T value, int statusCode) => new(value, statusCode, null, null);

    public static ApiResult<T> Failed(int? statusCode, HttpValidationProblemDetails? problem, string message) =>
        new(default, statusCode, problem, message);

    /// <summary>Correlation id reported by the back-end in the problem document (if any).</summary>
    public string? ProblemCorrelationId =>
        Problem?.Extensions.TryGetValue("correlationId", out var value) == true ? value?.ToString() : null;
}
