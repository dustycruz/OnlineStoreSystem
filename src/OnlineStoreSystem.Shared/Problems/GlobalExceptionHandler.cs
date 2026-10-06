// File: src/OnlineStoreSystem.Shared/Problems/GlobalExceptionHandler.cs
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnlineStoreSystem.Shared.Http;
using MvcProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OnlineStoreSystem.Shared.Problems;

/// <summary>
/// Converts unhandled exceptions into RFC 7807 problem details (via <see cref="IProblemDetailsService"/>,
/// so correlation id / trace id extensions are added consistently). Internal error details are never
/// leaked: clients receive the correlation id to quote instead.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IOptions<ExceptionProblemMappingOptions> mappingOptions,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            DependencyUnavailableException dependency => new MvcProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = ProblemTypes.DependencyUnavailable,
                Title = $"{dependency.Dependency} is unavailable",
                Detail = $"{dependency.Message} The request was retried with exponential backoff before giving up; please try again later.",
                Extensions = { ["dependency"] = dependency.Dependency }
            },
            BadHttpRequestException badRequest => new MvcProblemDetails
            {
                Status = badRequest.StatusCode,
                Type = ProblemTypes.Validation,
                Title = "Bad request",
                Detail = badRequest.Message
            },
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => new MvcProblemDetails
            {
                Status = 499,
                Title = "Client closed request"
            },
            _ => MapConfigured(exception)
        };

        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        if (status >= 500 && exception is not DependencyUnavailableException)
        {
            logger.LogError(exception, "Request failed with {StatusCode}: {ExceptionType}", status, exception.GetType().Name);
        }
        else
        {
            logger.LogWarning("Request failed with {StatusCode}: {ExceptionType}: {Message}",
                status, exception.GetType().Name, exception.Message);
        }

        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "5";
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem
        });
    }

    private MvcProblemDetails MapConfigured(Exception exception)
    {
        var mapping = mappingOptions.Value.Find(exception);
        if (mapping is not null)
        {
            return new MvcProblemDetails
            {
                Status = mapping.StatusCode,
                Type = mapping.Type,
                Title = mapping.Title,
                Detail = mapping.ExposeMessage ? exception.Message : null
            };
        }

        return new MvcProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Type = ProblemTypes.InternalError,
            Title = "An unexpected error occurred",
            Detail = "The error has been logged. Quote the correlationId when contacting support."
        };
    }
}
