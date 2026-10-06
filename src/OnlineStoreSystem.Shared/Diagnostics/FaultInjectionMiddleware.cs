// File: src/OnlineStoreSystem.Shared/Diagnostics/FaultInjectionMiddleware.cs
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Problems;

namespace OnlineStoreSystem.Shared.Diagnostics;

/// <summary>See <see cref="FaultInjectionOptions"/>. Never enable in production.</summary>
public sealed class FaultInjectionMiddleware(
    RequestDelegate next,
    IOptionsMonitor<FaultInjectionOptions> options,
    ILogger<FaultInjectionMiddleware> logger)
{
    private const int MaxTrackedCorrelationIds = 10_000;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled || !context.Request.Path.StartsWithSegments(settings.PathPrefix.TrimEnd('/')))
        {
            await next(context);
            return;
        }

        var correlationId = context.CorrelationId ?? "(none)";
        if (_attempts.Count > MaxTrackedCorrelationIds)
        {
            _attempts.Clear();
        }

        var attempt = _attempts.AddOrUpdate(correlationId, 1, static (_, current) => current + 1);
        if (attempt <= settings.FailuresPerCorrelationId)
        {
            logger.LogWarning(
                "FAULT INJECTION: failing attempt {Attempt}/{Failures} for correlation id {CorrelationId} with {StatusCode}",
                attempt, settings.FailuresPerCorrelationId, correlationId, settings.StatusCode);

            context.Response.StatusCode = settings.StatusCode;
            var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails
                {
                    Status = settings.StatusCode,
                    Type = ProblemTypes.FaultInjected,
                    Title = "Injected transient failure",
                    Detail = $"Attempt {attempt} of {settings.FailuresPerCorrelationId} injected failures for this correlation id."
                }
            });
            return;
        }

        logger.LogInformation("FAULT INJECTION: attempt {Attempt} for correlation id {CorrelationId} passes through",
            attempt, correlationId);
        await next(context);
    }
}

public static class FaultInjectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddFaultInjection(Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            services.AddOptions<FaultInjectionOptions>()
                .Bind(configuration.GetSection(FaultInjectionOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            return services;
        }
    }

    extension(IApplicationBuilder app)
    {
        public IApplicationBuilder UseFaultInjection()
        {
            var settings = app.ApplicationServices.GetRequiredService<IOptions<FaultInjectionOptions>>().Value;
            if (settings.Enabled)
            {
                app.ApplicationServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger<FaultInjectionMiddleware>()
                    .LogWarning(
                        "FAULT INJECTION ENABLED: the first {Failures} request(s) of every correlation id under {PathPrefix} answer {StatusCode}",
                        settings.FailuresPerCorrelationId, settings.PathPrefix, settings.StatusCode);
            }

            return app.UseMiddleware<FaultInjectionMiddleware>();
        }
    }
}
