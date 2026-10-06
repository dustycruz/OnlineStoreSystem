// File: src/OnlineStoreSystem.Shared/Messaging/IRabbitMqConnectionProvider.cs
using RabbitMQ.Client;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// One long-lived AMQP connection per process (connections are expensive, channels are cheap).
/// </summary>
public interface IRabbitMqConnectionProvider
{
    /// <summary>True when a connection exists and is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>Returns the shared connection, creating it on first use.</summary>
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken);
}
