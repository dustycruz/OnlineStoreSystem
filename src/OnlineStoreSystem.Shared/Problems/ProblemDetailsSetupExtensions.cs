// File: src/OnlineStoreSystem.Shared/Problems/ProblemDetailsSetupExtensions.cs
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OnlineStoreSystem.Shared.Correlation;

namespace OnlineStoreSystem.Shared.Problems;

public static class ProblemDetailsSetupExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Enables RFC 7807 problem details for: model validation (400), MVC client errors (404/409...),
        /// empty-body status codes (via <c>UseStatusCodePages</c>) and unhandled exceptions
        /// (via <see cref="GlobalExceptionHandler"/>). Every problem gets <c>instance</c>,
        /// <c>correlationId</c>, <c>traceId</c>, <c>service</c> and <c>timestamp</c>.
        /// </summary>
        public IServiceCollection AddOnlineStoreProblemDetails(
            string serviceName,
            Action<ExceptionProblemMappingOptions>? configureExceptionMappings = null)
        {
            services.AddProblemDetails(options =>
            {
                options.CustomizeProblemDetails = context =>
                {
                    var http = context.HttpContext;
                    context.ProblemDetails.Instance ??= http.Request.Path;
                    context.ProblemDetails.Extensions["correlationId"] =
                        http.CorrelationId ?? http.Request.Headers[CorrelationIdConstants.HeaderName].FirstOrDefault();
                    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.Id ?? http.TraceIdentifier);
                    context.ProblemDetails.Extensions["service"] = serviceName;
                    context.ProblemDetails.Extensions["timestamp"] = DateTimeOffset.UtcNow;
                };
            });

            services.AddOptions<ExceptionProblemMappingOptions>()
                .Configure(options => configureExceptionMappings?.Invoke(options));

            services.AddExceptionHandler<GlobalExceptionHandler>();

            // MVC's default ClientErrorMapping already links 400/404/409/... to the RFC 9110 sections used in
            // ProblemTypes, and [ApiController] turns invalid models into ValidationProblemDetails (RFC 7807).
            return services;
        }
    }
}
