// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqConnectionProvider.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>
/// Lazily opens and caches the process-wide <see cref="IConnection"/>. Once created, the connection
/// is never replaced: RabbitMQ.Client's automatic recovery re-establishes it (and its channels,
/// queues, bindings and consumers) after network failures or broker restarts.
/// </summary>
public sealed class RabbitMqConnectionProvider(
    IOptions<RabbitMqOptions> options,
    IHostEnvironment environment,
    ILogger<RabbitMqConnectionProvider> logger) : IRabbitMqConnectionProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public bool IsConnected => _connection?.IsOpen == true;

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var settings = options.Value;
            var clientName = settings.ClientProvidedName ?? environment.ApplicationName;
            var factory = RabbitMqConnectionFactoryHelper.Create(settings, clientName);

            logger.LogInformation("Connecting to RabbitMQ at {Endpoint} as {ClientName}...",
                settings.DescribeEndpoint(), clientName);

            var connection = await factory.CreateConnectionAsync(clientName, cancellationToken);

            connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
            connection.ConnectionRecoveryErrorAsync += OnConnectionRecoveryErrorAsync;

            logger.LogInformation("Connected to RabbitMQ at {Endpoint}", settings.DescribeEndpoint());
            _connection = connection;
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        if (args.Initiator != ShutdownInitiator.Application)
        {
            logger.LogWarning("RabbitMQ connection lost ({ReplyCode} {ReplyText}); automatic recovery will retry",
                args.ReplyCode, args.ReplyText);
        }

        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
    {
        logger.LogInformation("RabbitMQ connection recovered");
        return Task.CompletedTask;
    }

    private Task OnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs args)
    {
        logger.LogWarning(args.Exception, "RabbitMQ connection recovery attempt failed");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error while closing RabbitMQ connection");
            }

            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();
    }
}
