// File: src/OnlineStoreSystem.Shared/Correlation/CorrelationIdExtensions.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OnlineStoreSystem.Shared.Correlation;

/// <summary>
/// Registration helpers written with C# 14 extension members.
/// </summary>
public static class CorrelationIdExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Registers the ambient accessor and the outgoing DelegatingHandler.</summary>
        public IServiceCollection AddCorrelationId()
        {
            services.TryAddSingleton<ICorrelationIdAccessor, CorrelationIdAccessor>();
            services.TryAddTransient<CorrelationIdDelegatingHandler>();
            return services;
        }
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>Adds <see cref="CorrelationIdMiddleware"/>; call it first in the pipeline.</summary>
        public IApplicationBuilder UseCorrelationId() => app.UseMiddleware<CorrelationIdMiddleware>();
    }

    extension(IHttpClientBuilder builder)
    {
        /// <summary>Propagates <c>X-Correlation-Id</c> on every request of this typed/named client.</summary>
        public IHttpClientBuilder AddCorrelationIdPropagation() =>
            builder.AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
    }

    extension(HttpContext context)
    {
        /// <summary>The correlation id assigned by <see cref="CorrelationIdMiddleware"/>, if any.</summary>
        public string? CorrelationId =>
            context.Items.TryGetValue(CorrelationIdConstants.HttpContextItemKey, out var value) ? value as string : null;
    }
}
