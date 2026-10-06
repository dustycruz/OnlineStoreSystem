// File: src/OnlineStoreSystem.Shared/Hosting/HealthCheckEndpointExtensions.cs
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OnlineStoreSystem.Shared.Hosting;

/// <summary>
/// <c>/health</c> (readiness: database, broker...) and <c>/health/live</c> (process is up) with a JSON body.
/// </summary>
public static class HealthCheckEndpointExtensions
{
    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapOnlineStoreHealthChecks(string serviceName)
        {
            endpoints.MapHealthChecks("/health", new HealthCheckOptions
            {
                ResponseWriter = (context, report) => WriteJsonAsync(context, report, serviceName)
            });

            endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false,
                ResponseWriter = (context, report) => WriteJsonAsync(context, report, serviceName)
            });

            return endpoints;
        }
    }

    private static Task WriteJsonAsync(HttpContext context, HealthReport report, string serviceName)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            service = serviceName,
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                error = entry.Value.Exception?.Message
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
    }
}
