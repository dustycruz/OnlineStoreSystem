// File: src/OnlineStoreSystem.InventoryService/Services/ProcessedMessagesIdempotencyGuard.cs
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.InventoryService.Data;
using OnlineStoreSystem.InventoryService.Models;
using OnlineStoreSystem.Shared.Messaging;

namespace OnlineStoreSystem.InventoryService.Services;

/// <summary>
/// Idempotent-consumer pattern backed by <c>InventoryDb.dbo.ProcessedMessages</c>.
/// <para>Inside ONE database transaction it:</para>
/// <list type="number">
///   <item>looks up (MessageId, Consumer) in ProcessedMessages - if found the message is a redelivery and
///         <c>onDuplicate</c> runs instead (no stock is touched twice);</item>
///   <item>runs the business change (<c>process</c>);</item>
///   <item>inserts the (MessageId, Consumer) ledger row;</item>
///   <item>commits - change and ledger row become visible atomically.</item>
/// </list>
/// Two consumers racing on the same message are resolved by the primary key: the loser gets a
/// unique-key violation and is treated as a duplicate.
/// The whole unit is wrapped in EF Core's execution strategy so transient SQL errors are retried.
/// </summary>
public sealed class ProcessedMessagesIdempotencyGuard(
    InventoryDbContext db,
    TimeProvider clock,
    ILogger<ProcessedMessagesIdempotencyGuard> logger)
{
    public async Task<TResult> ExecuteOnceAsync<TResult>(
        MessageContext context,
        Func<CancellationToken, Task<TResult>> process,
        Func<CancellationToken, Task<TResult>> onDuplicate,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                var alreadyProcessed = await db.ProcessedMessages
                    .AsNoTracking()
                    .AnyAsync(m => m.MessageId == context.MessageId && m.Consumer == context.ConsumerName, cancellationToken);

                if (alreadyProcessed)
                {
                    logger.LogWarning(
                        "IDEMPOTENCY: {MessageType} {MessageId} was already processed by {Consumer} - skipping business logic",
                        context.MessageType, context.MessageId, context.ConsumerName);
                    var duplicateResult = await onDuplicate(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return duplicateResult;
                }

                var result = await process(cancellationToken);

                db.ProcessedMessages.Add(new ProcessedMessage
                {
                    MessageId = context.MessageId,
                    Consumer = context.ConsumerName,
                    MessageType = context.MessageType,
                    CorrelationId = context.CorrelationId,
                    ProcessedAtUtc = clock.GetUtcNow().UtcDateTime
                });

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation("IDEMPOTENCY: recorded {MessageId} for {Consumer} in ProcessedMessages",
                    context.MessageId, context.ConsumerName);
                return result;
            });
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation)
        {
            logger.LogWarning(
                "IDEMPOTENCY: concurrent processing of {MessageId} detected (unique key violation) - treating as duplicate",
                context.MessageId);
            db.ChangeTracker.Clear();
            return await onDuplicate(cancellationToken);
        }
    }
}
