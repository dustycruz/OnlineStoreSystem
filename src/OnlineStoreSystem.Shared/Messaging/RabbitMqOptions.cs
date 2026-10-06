// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqOptions.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Bound from the <c>RabbitMq</c> configuration section of every service.
/// Either set <see cref="ConnectionString"/> (e.g. a CloudAMQP <c>amqps://</c> URL) or the
/// discrete host settings for a locally installed broker. No Docker is required.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>
    /// Full AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672/</c> or
    /// <c>amqps://user:pass@hostname.rmq.cloudamqp.com/vhost</c>. Takes precedence when set.
    /// </summary>
    public string? ConnectionString { get; set; }

    [Required]
    public string HostName { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    [Required]
    public string UserName { get; set; } = "guest";

    [Required]
    public string Password { get; set; } = "guest";

    [Required]
    public string VirtualHost { get; set; } = "/";

    /// <summary>Durable topic exchange all integration events are published to.</summary>
    [Required]
    public string ExchangeName { get; set; } = "onlinestore.events";

    /// <summary>Dead-letter exchange (direct). Poison messages are routed here on BasicNack(requeue: false).</summary>
    [Required]
    public string DeadLetterExchangeName { get; set; } = "onlinestore.dlx";

    /// <summary>Suffix appended to a queue name to build its dead-letter queue name.</summary>
    [Required]
    public string DeadLetterQueueSuffix { get; set; } = ".dlq";

    /// <summary>Maximum un-acknowledged deliveries per consumer channel (BasicQos).</summary>
    [Range(1, 1000)]
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>How long a publish waits for the broker's publisher confirm.</summary>
    [Range(1, 300)]
    public int PublishConfirmTimeoutSeconds { get; set; } = 10;

    /// <summary>Delay between attempts when the broker is unreachable at startup.</summary>
    [Range(1, 300)]
    public int ConnectRetryDelaySeconds { get; set; } = 5;

    /// <summary>Delay before a failed (non-poison) delivery is NACKed with requeue: true.</summary>
    [Range(0, 60_000)]
    public int RequeueDelayMilliseconds { get; set; } = 1000;

    /// <summary>Shown in the RabbitMQ management UI (Connections tab). Defaults to the service name.</summary>
    public string? ClientProvidedName { get; set; }

    /// <summary>Human-readable endpoint WITHOUT credentials, for logs.</summary>
    public string DescribeEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString) && Uri.TryCreate(ConnectionString, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}:{(uri.IsDefaultPort ? (uri.Scheme == "amqps" ? 5671 : 5672) : uri.Port)}{uri.AbsolutePath}";
        }

        return $"amqp://{HostName}:{Port}{(VirtualHost.StartsWith('/') ? VirtualHost : "/" + VirtualHost)}";
    }
}
