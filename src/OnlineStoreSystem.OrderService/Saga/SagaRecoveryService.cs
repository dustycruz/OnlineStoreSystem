// File: src/OnlineStoreSystem.OrderService/Saga/SagaRecoveryService.cs
using Microsoft.Extensions.Options;
using OnlineStoreSystem.Shared.Correlation;

namespace OnlineStoreSystem.OrderService.Saga;

/// <summary>
/// Periodically resumes sagas that are stuck: payment step interrupted / PaymentService down, no reply from
/// InventoryService before the timeout, or a compensation that was never confirmed. Each saga is resumed in its
/// own DI scope with its ORIGINAL correlation id restored, so the retry is traceable end-to-end.
/// </summary>
public sealed class SagaRecoveryService(
    IServiceScopeFactory scopeFactory,
    ICorrelationIdAccessor correlationIdAccessor,
    IOptions<SagaOptions> options,
    ILogger<SagaRecoveryService> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.RecoveryPollIntervalSeconds);
        logger.LogInformation("Saga recovery worker started (scan every {Interval})", interval);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RecoverOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StalledSaga> stalled;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<OrderSagaOrchestrator>();
            stalled = await orchestrator.FindStalledSagasAsync(BatchSize, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Saga recovery scan failed; will retry on the next tick");
            return;
        }

        foreach (var saga in stalled)
        {
            var correlationId = saga.CorrelationId ?? CorrelationIdGenerator.NewId();
            correlationIdAccessor.CorrelationId = correlationId;
            using var logScope = logger.BeginScope(new Dictionary<string, object>
            {
                [CorrelationIdConstants.LogPropertyName] = correlationId,
                ["SagaId"] = saga.SagaId
            });

            try
            {
                logger.LogInformation("Recovery worker resuming saga {SagaId} of order {OrderId}", saga.SagaId, saga.OrderId);
                await using var scope = scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<OrderSagaOrchestrator>();
                await orchestrator.ResumeAsync(saga.SagaId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recovery of saga {SagaId} failed; it will be retried", saga.SagaId);
            }
            finally
            {
                correlationIdAccessor.CorrelationId = null;
            }
        }
    }
}
