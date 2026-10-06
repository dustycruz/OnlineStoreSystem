// File: src/OnlineStoreSystem.OrderService/Outbox/OutboxDispatcher.cs
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnlineStoreSystem.OrderService.Data;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Messaging;

namespace OnlineStoreSystem.OrderService.Outbox;

/// <summary>
/// Transactional-outbox relay. Reads pending <c>OutboxMessages</c> in creation order and publishes them through
/// <see cref="RabbitMqEventPublisher"/> (publisher confirms). A row is marked processed only AFTER the broker
/// confirmed it, so a crash or a broker outage never loses an event (at-least-once; consumers are idempotent).
/// On a failure the batch stops to preserve ordering and is retried after a back-off.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    OutboxSignal signal,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var settings = options.Value;
        logger.LogInformation("Outbox dispatcher started (batch {BatchSize}, poll every {PollMs} ms)",
            settings.BatchSize, settings.PollIntervalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var (dispatched, failed) = (0, false);
            try
            {
                (dispatched, failed) = await DispatchPendingAsync(settings.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch cycle failed");
                failed = true;
            }

            if (!failed && dispatched == settings.BatchSize)
            {
                continue; // a full batch - there is probably more waiting
            }

            var wait = TimeSpan.FromMilliseconds(failed ? settings.ErrorBackoffMilliseconds : settings.PollIntervalMilliseconds);
            try
            {
                await signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<(int Dispatched, bool Failed)> DispatchPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        var pending = await db.OutboxMessages
            .Where(m => m.ProcessedAtUtc == null)
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.OutboxMessageId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var dispatched = 0;
        foreach (var message in pending)
        {
            using var logScope = logger.BeginScope(new Dictionary<string, object>
            {
                [CorrelationIdConstants.LogPropertyName] = message.CorrelationId ?? string.Empty,
                ["MessageId"] = message.OutboxMessageId
            });

            try
            {
                var occurredAt = new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAtUtc, DateTimeKind.Utc));
                await publisher.PublishAsync(
                    new OutgoingMessage(
                        message.OutboxMessageId,
                        message.MessageType,
                        message.RoutingKey,
                        Encoding.UTF8.GetBytes(message.Payload),
                        message.CorrelationId,
                        occurredAt),
                    cancellationToken);

                message.Attempts += 1;
                message.ProcessedAtUtc = clock.GetUtcNow().UtcDateTime;
                message.LastError = null;
                await db.SaveChangesAsync(cancellationToken);
                dispatched++;

                logger.LogInformation("Outbox relayed {MessageType} {MessageId} (attempt {Attempts})",
                    message.MessageType, message.OutboxMessageId, message.Attempts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                message.Attempts += 1;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                await db.SaveChangesAsync(CancellationToken.None);

                logger.LogWarning(ex,
                    "Outbox could not publish {MessageType} {MessageId} (attempt {Attempts}); it stays pending and will be retried",
                    message.MessageType, message.OutboxMessageId, message.Attempts);
                return (dispatched, true);
            }
        }

        return (dispatched, false);
    }
}
