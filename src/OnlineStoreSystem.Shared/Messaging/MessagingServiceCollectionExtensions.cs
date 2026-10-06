// File: src/OnlineStoreSystem.Shared/Messaging/MessagingServiceCollectionExtensions.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnlineStoreSystem.Shared.Correlation;

namespace OnlineStoreSystem.Shared.Messaging;

public static class MessagingServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Binds and validates <see cref="RabbitMqOptions"/> and registers the shared connection
        /// provider and the confirm-based <see cref="IEventPublisher"/>. Consumers are registered by
        /// each service with <c>AddHostedService&lt;TConsumer&gt;()</c>.
        /// </summary>
        public IServiceCollection AddRabbitMqMessaging(IConfiguration configuration)
        {
            services.AddOptions<RabbitMqOptions>()
                .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddCorrelationId();
            services.TryAddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
            services.TryAddSingleton<IEventPublisher, RabbitMqEventPublisher>();
            return services;
        }
    }
}
