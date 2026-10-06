// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqConnectionFactoryHelper.cs
using RabbitMQ.Client;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Builds a correctly configured <see cref="ConnectionFactory"/> from <see cref="RabbitMqOptions"/>.
/// Automatic connection + topology recovery means consumers resume by themselves after a broker restart.
/// </summary>
public static class RabbitMqConnectionFactoryHelper
{
    public static ConnectionFactory Create(RabbitMqOptions options, string clientProvidedName)
    {
        ArgumentNullException.ThrowIfNull(options);

        var factory = new ConnectionFactory
        {
            ClientProvidedName = clientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(15),
            // Deliveries are processed one at a time per channel => in-order, simple reasoning.
            ConsumerDispatchConcurrency = 1
        };

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            // amqps:// automatically enables TLS (CloudAMQP, Amazon MQ, Azure-hosted brokers...).
            factory.Uri = new Uri(options.ConnectionString);
        }
        else
        {
            factory.HostName = options.HostName;
            factory.Port = options.Port;
            factory.UserName = options.UserName;
            factory.Password = options.Password;
            factory.VirtualHost = options.VirtualHost;
        }

        return factory;
    }
}
