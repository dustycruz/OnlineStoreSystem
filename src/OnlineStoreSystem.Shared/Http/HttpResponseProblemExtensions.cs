// File: src/OnlineStoreSystem.Shared/Http/HttpResponseProblemExtensions.cs
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OnlineStoreSystem.Shared.Json;

namespace OnlineStoreSystem.Shared.Http;

/// <summary>Helpers for typed clients consuming RFC 7807 responses of downstream services.</summary>
public static class HttpResponseProblemExtensions
{
    extension(HttpResponseMessage response)
    {
        /// <summary>
        /// Reads an <c>application/problem+json</c> body (including validation <c>errors</c>);
        /// returns null when the body is empty or not a problem document.
        /// </summary>
        public async Task<HttpValidationProblemDetails?> ReadProblemAsync(CancellationToken cancellationToken)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(
                    JsonDefaults.Web, cancellationToken);
                return problem;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    extension(Microsoft.AspNetCore.Mvc.ProblemDetails problem)
    {
        /// <summary>Reads a string extension member (e.g. <c>declineCode</c>) from a deserialized problem.</summary>
        public string? GetExtensionString(string name) =>
            problem.Extensions.TryGetValue(name, out var value)
                ? value switch
                {
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                    JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                    JsonElement element => element.ToString(),
                    null => null,
                    _ => value.ToString()
                }
                : null;
    }
}
