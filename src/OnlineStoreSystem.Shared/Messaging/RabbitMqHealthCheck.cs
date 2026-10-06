// File: src/OnlineStoreSystem.Shared/Messaging/RabbitMqHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>Reports whether the process holds an open AMQP connection.</summary>
public sealed class RabbitMqHealthCheck(
    IRabbitMqConnectionProvider connectionProvider,
    IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var endpoint = options.Value.DescribeEndpoint();
        if (connectionProvider.IsConnected)
        {
            return HealthCheckResult.Healthy($"Connected to {endpoint}");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var connection = await connectionProvider.GetConnectionAsync(timeout.Token);
            return connection.IsOpen
                ? HealthCheckResult.Healthy($"Connected to {endpoint}")
                : HealthCheckResult.Degraded($"Connection to {endpoint} is recovering");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, $"Cannot reach RabbitMQ at {endpoint}", ex);
        }
    }
}
