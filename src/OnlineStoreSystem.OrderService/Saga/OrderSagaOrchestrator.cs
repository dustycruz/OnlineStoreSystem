// File: src/OnlineStoreSystem.OrderService/Saga/OrderSagaOrchestrator.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnlineStoreSystem.OrderService.Application;
using OnlineStoreSystem.OrderService.Clients;
using OnlineStoreSystem.OrderService.Data;
using OnlineStoreSystem.OrderService.Data.Entities;
using OnlineStoreSystem.OrderService.Outbox;
using OnlineStoreSystem.Shared.Contracts.Orders;
using OnlineStoreSystem.Shared.Contracts.Payments;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Events;
using OnlineStoreSystem.Shared.Identifiers;
using OnlineStoreSystem.Shared.Messaging;

namespace OnlineStoreSystem.OrderService.Saga;

/// <summary>A saga that the recovery worker should look at.</summary>
public sealed record StalledSaga(Guid SagaId, Guid OrderId, string? CorrelationId);

/// <summary>
/// <b>Orchestrated checkout saga</b> (Module 5). OrderService is the single coordinator; the state machine is
/// persisted in <c>OrderDb.SagaStates</c> (+ audit trail in <c>SagaStepLogs</c>) and every event it emits is
/// written to the transactional outbox in the SAME transaction as the state change.
/// <code>
///  STEP                     MECHANISM                                   ON FAILURE / COMPENSATION
///  1 ValidateProducts       sync  HTTP → CatalogService (resilient)       400 / 503 to the caller, nothing persisted
///  2 CreateOrder (Pending)  local ACID transaction (+ outbox row)        -
///  3 ReserveStock           async OrderPlacedEvent → InventoryService     StockReservationFailed → order Cancelled
///                                 ← StockReservedEvent / ...Failed      timeout → OrderCancelledEvent (release)
///  4 ProcessPayment         sync  HTTP → PaymentService (resilient,       declined (402) → COMPENSATE:
///                                 Idempotency-Key)                        PaymentFailedEvent → ReleaseStock
///                                                                         order Cancelled, saga Compensating
///                                                                         ← StockReleasedEvent → Compensated
///  5 CompleteOrder          order Completed + PaymentProcessedEvent      (InventoryService commits the stock)
/// </code>
/// Idempotency of replies: a reply is only applied when the saga is in the matching step; duplicates
/// and out-of-order replies are ignored. Concurrent transitions are rejected by the RowVersion token.
/// </summary>
public sealed class OrderSagaOrchestrator(
    OrderDbContext db,
    ProductValidator productValidator,
    PaymentClient paymentClient,
    PaymentDetailsProtector paymentDetailsProtector,
    OutboxSignal outboxSignal,
    ICorrelationIdAccessor correlationIdAccessor,
    IOptions<SagaOptions> options,
    TimeProvider clock,
    ILogger<OrderSagaOrchestrator> logger)
{
    // Column sizes from sql/04_OrderDb.sql - values coming from other services are truncated to fit.
    private const int FailureReasonMaxLength = 1000;
    private const int LastErrorMaxLength = 2000;
    private const int StepDetailMaxLength = 2000;

    private DateTime _lastStepTimestamp = DateTime.MinValue;
    private bool _outboxPending;

    // =====================================================================================================
    // Saga start
    // =====================================================================================================

    /// <summary>One-shot checkout: validate products, create the order as Pending and start the saga.</summary>
    public async Task<OrderCommandResult> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("SAGA START: checkout for customer {CustomerId} with {LineCount} line(s)",
            request.CustomerId, request.Items.Count);

        // STEP 1 - Validate Product (synchronous, resilient HTTP call to CatalogService).
        // DependencyUnavailableException propagates => 503 problem details; nothing has been persisted.
        var validation = await productValidator.ValidateAsync(request.Items, cancellationToken);
        if (!validation.IsValid)
        {
            return OrderCommandResult.Invalid(validation.Errors);
        }

        // STEP 2 - Create Order (Pending) + saga + OrderPlacedEvent in ONE local transaction.
        var now = Now();
        var order = new Order
        {
            OrderId = SequentialGuid.NewGuid(),
            OrderNumber = OrderNumberGenerator.Next(now),
            CustomerId = request.CustomerId.Trim(),
            CustomerEmail = request.CustomerEmail.Trim(),
            Status = OrderStatuses.Pending,
            CorrelationId = correlationIdAccessor.CorrelationId,
            CreatedAtUtc = now
        };
        OrderLines.Apply(order, validation.Lines, validation.Currency);
        db.Orders.Add(order);

        var saga = BeginSaga(order, request.Payment!, validation, now);

        await db.SaveChangesAsync(cancellationToken);
        NotifyOutbox();

        logger.LogInformation(
            "SAGA {SagaId}: order {OrderNumber} ({OrderId}) created as Pending, total {Total:F2} {Currency}; waiting for stock reservation",
            saga.SagaId, order.OrderNumber, order.OrderId, order.TotalAmount, order.Currency);

        return OrderCommandResult.Accepted(order);
    }

    /// <summary>Checkout of an existing DRAFT order: re-validate / re-price and start the saga.</summary>
    public async Task<OrderCommandResult> SubmitDraftAsync(
        Guid orderId,
        SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .Include(o => o.OrderItems)
            .Include(o => o.SagaState)
            .SingleOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

        if (order is null)
        {
            return OrderCommandResult.Missing(orderId);
        }

        if (order.Status != OrderStatuses.Draft || order.SagaState is not null)
        {
            return OrderCommandResult.Conflicting(
                $"Order {order.OrderNumber} is {order.Status}; only Draft orders can be checked out.");
        }

        logger.LogInformation("SAGA START: checkout of draft order {OrderNumber} ({OrderId})", order.OrderNumber, order.OrderId);

        var lines = order.OrderItems
            .Select(item => new OrderLineRequest { ProductId = item.ProductId, Quantity = item.Quantity })
            .ToList();
        var validation = await productValidator.ValidateAsync(lines, cancellationToken);
        if (!validation.IsValid)
        {
            return OrderCommandResult.Invalid(validation.Errors);
        }

        var now = Now();
        OrderLines.Apply(order, validation.Lines, validation.Currency); // refresh prices from the catalog
        order.Status = OrderStatuses.Pending;
        order.UpdatedAtUtc = now;
        order.CorrelationId = correlationIdAccessor.CorrelationId ?? order.CorrelationId;

        var saga = BeginSaga(order, request.Payment!, validation, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueConstraintViolation)
        {
            return OrderCommandResult.Conflicting(
                $"Order {order.OrderNumber} was modified or checked out concurrently. Reload it and retry.");
        }

        NotifyOutbox();
        logger.LogInformation("SAGA {SagaId}: draft order {OrderNumber} submitted (total {Total:F2} {Currency})",
            saga.SagaId, order.OrderNumber, order.TotalAmount, order.Currency);
        return OrderCommandResult.Accepted(order);
    }

    private SagaState BeginSaga(Order order, PaymentDetailsRequest payment, ProductValidationResult validation, DateTime now)
    {
        var saga = new SagaState
        {
            SagaId = SequentialGuid.NewGuid(),
            OrderId = order.OrderId,
            Order = order,
            Status = SagaStatuses.Running,
            CurrentStep = SagaSteps.ReserveStock,
            StockReserved = false,
            PaymentProcessed = false,
            ProtectedPaymentDetails = paymentDetailsProtector.Protect(payment),
            PaymentCardLast4 = PaymentDetailsProtector.Last4(payment),
            RetryCount = 0,
            CorrelationId = order.CorrelationId,
            StartedAtUtc = now,
            UpdatedAtUtc = now
        };
        order.SagaState = saga;
        db.SagaStates.Add(saga);

        AddStep(saga, SagaSteps.ValidateProducts, SagaStepOutcomes.Succeeded,
            $"CatalogService validated {validation.Lines.Count} product(s): {validation.Describe()}");
        AddStep(saga, SagaSteps.CreateOrder, SagaStepOutcomes.Succeeded,
            $"Order {order.OrderNumber} stored as Pending; total {order.TotalAmount:F2} {order.Currency}");
        AddStep(saga, SagaSteps.ReserveStock, SagaStepOutcomes.Started,
            "OrderPlacedEvent written to the transactional outbox; awaiting InventoryService");

        // STEP 3 - Reserve Stock (asynchronous): the event is committed together with the order.
        Enqueue(new OrderPlacedEvent
        {
            CorrelationId = order.CorrelationId,
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            CustomerId = order.CustomerId,
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            Items = order.OrderItems
                .OrderBy(item => item.ProductId)
                .Select(item => new OrderLineItem(item.ProductId, item.Sku, item.Quantity, item.UnitPrice))
                .ToList()
        }, RoutingKeys.OrderPlaced);

        return saga;
    }

    // =====================================================================================================
    // Replies from InventoryService
    // =====================================================================================================

    public async Task HandleStockReservedAsync(StockReservedEvent reply, CancellationToken cancellationToken)
    {
        var saga = await LoadSagaAsync(reply.OrderId, cancellationToken);
        if (saga is null)
        {
            logger.LogWarning("StockReservedEvent for unknown order {OrderId} ignored", reply.OrderId);
            return;
        }

        var order = saga.Order;

        if (saga.Status == SagaStatuses.Running && saga.CurrentStep == SagaSteps.ReserveStock)
        {
            var now = Now();
            saga.StockReserved = true;
            saga.CurrentStep = SagaSteps.ProcessPayment;
            saga.RetryCount = 0;
            saga.LastError = null;
            saga.UpdatedAtUtc = now;
            order.Status = OrderStatuses.StockReserved;
            order.UpdatedAtUtc = now;

            AddStep(saga, SagaSteps.ReserveStock, SagaStepOutcomes.Succeeded,
                $"InventoryService reserved {Describe(reply.Items)}");
            AddStep(saga, SagaSteps.ProcessPayment, SagaStepOutcomes.Started,
                $"Charging {order.TotalAmount:F2} {order.Currency} via PaymentService (card ****{saga.PaymentCardLast4})");

            if (!await TrySaveAsync(saga, cancellationToken))
            {
                return;
            }

            // STEP 4 - Process Payment (synchronous).
            await ExecutePaymentStepAsync(saga, cancellationToken);
            return;
        }

        if (order.Status == OrderStatuses.Cancelled && !saga.StockReserved)
        {
            // The saga gave up waiting (timeout) but InventoryService reserved later: release that stock too.
            saga.UpdatedAtUtc = Now();
            AddStep(saga, SagaSteps.ReleaseStock, SagaStepOutcomes.Compensating,
                $"Late reservation ({Describe(reply.Items)}) arrived after cancellation; OrderCancelledEvent queued to release it");
            Enqueue(new OrderCancelledEvent
            {
                CorrelationId = saga.CorrelationId,
                OrderId = order.OrderId,
                Reason = "Stock was reserved after the order had already been cancelled"
            }, RoutingKeys.OrderCancelled);

            if (await TrySaveAsync(saga, cancellationToken))
            {
                logger.LogWarning("SAGA {SagaId}: late stock reservation for cancelled order {OrderNumber} will be released",
                    saga.SagaId, order.OrderNumber);
            }

            return;
        }

        logger.LogInformation(
            "Duplicate / out-of-order StockReservedEvent for order {OrderNumber} ignored (saga {Status} at {Step}, replay: {IsReplay})",
            order.OrderNumber, saga.Status, saga.CurrentStep, reply.IsReplay);
    }

    public async Task HandleStockReservationFailedAsync(StockReservationFailedEvent reply, CancellationToken cancellationToken)
    {
        var saga = await LoadSagaAsync(reply.OrderId, cancellationToken);
        if (saga is null)
        {
            logger.LogWarning("StockReservationFailedEvent for unknown order {OrderId} ignored", reply.OrderId);
            return;
        }

        if (saga.Status != SagaStatuses.Running || saga.CurrentStep != SagaSteps.ReserveStock)
        {
            logger.LogInformation("Duplicate / out-of-order StockReservationFailedEvent for order {OrderId} ignored (saga {Status} at {Step})",
                reply.OrderId, saga.Status, saga.CurrentStep);
            return;
        }

        var order = saga.Order;
        var now = Now();
        saga.Status = SagaStatuses.Failed;
        saga.CurrentStep = SagaSteps.Done;
        saga.LastError = Truncate(reply.Reason, LastErrorMaxLength);
        saga.ProtectedPaymentDetails = null; // no payment will ever be attempted
        saga.CompletedAtUtc = now;
        saga.UpdatedAtUtc = now;
        order.Status = OrderStatuses.Cancelled;
        order.FailureReason = Truncate(reply.Reason, FailureReasonMaxLength);
        order.UpdatedAtUtc = now;

        AddStep(saga, SagaSteps.ReserveStock, SagaStepOutcomes.Failed, reply.Reason);
        AddStep(saga, SagaSteps.ProcessPayment, SagaStepOutcomes.Skipped,
            "Payment not attempted because stock could not be reserved - nothing to compensate");

        if (await TrySaveAsync(saga, cancellationToken))
        {
            logger.LogWarning("SAGA {SagaId} FAILED for order {OrderNumber}: {Reason}. Order Cancelled.",
                saga.SagaId, order.OrderNumber, reply.Reason);
        }
    }

    public async Task HandleStockReleasedAsync(StockReleasedEvent reply, CancellationToken cancellationToken)
    {
        var saga = await LoadSagaAsync(reply.OrderId, cancellationToken);
        if (saga is null)
        {
            logger.LogWarning("StockReleasedEvent for unknown order {OrderId} ignored", reply.OrderId);
            return;
        }

        var now = Now();
        var awaitingRelease = saga.Status == SagaStatuses.Compensating
                              || (saga.Status == SagaStatuses.Failed && saga.CurrentStep == SagaSteps.ReleaseStock);

        if (awaitingRelease)
        {
            saga.Status = SagaStatuses.Compensated;
            saga.CurrentStep = SagaSteps.Done;
            saga.CompletedAtUtc = now;
            saga.UpdatedAtUtc = now;
            AddStep(saga, SagaSteps.ReleaseStock, SagaStepOutcomes.Compensated,
                $"InventoryService released {Describe(reply.Items)} (triggered by {reply.TriggeredBy})");

            if (await TrySaveAsync(saga, cancellationToken))
            {
                logger.LogWarning("SAGA {SagaId} COMPENSATED for order {OrderNumber}: reserved stock released, order stays Cancelled",
                    saga.SagaId, saga.Order.OrderNumber);
            }

            return;
        }

        if (saga.Status == SagaStatuses.Compensated && reply.Items.Count > 0 && !reply.IsReplay)
        {
            saga.UpdatedAtUtc = now;
            AddStep(saga, SagaSteps.ReleaseStock, SagaStepOutcomes.Compensated,
                $"Late reservation released: {Describe(reply.Items)}");
            await TrySaveAsync(saga, cancellationToken);
            return;
        }

        logger.LogInformation("StockReleasedEvent for order {OrderId} ignored (saga {Status} at {Step}, replay: {IsReplay})",
            reply.OrderId, saga.Status, saga.CurrentStep, reply.IsReplay);
    }

    // =====================================================================================================
    // Step 4: payment + step 5 completion / compensation
    // =====================================================================================================

    private async Task ExecutePaymentStepAsync(SagaState saga, CancellationToken cancellationToken)
    {
        var order = saga.Order;
        var settings = options.Value;

        var card = paymentDetailsProtector.TryUnprotect(saga.ProtectedPaymentDetails);
        if (card is null)
        {
            await FailPaymentAndCompensateAsync(saga,
                "Payment details are no longer available (they could not be decrypted)", "payment_details_unavailable",
                paymentId: null, cancellationToken);
            return;
        }

        PaymentOutcome outcome;
        try
        {
            outcome = await paymentClient.ChargeAsync(
                new ChargePaymentRequest
                {
                    OrderId = order.OrderId,
                    Amount = order.TotalAmount,
                    Currency = order.Currency,
                    CardNumber = card.CardNumber,
                    CardHolderName = card.CardHolderName,
                    ExpiryMonth = card.ExpiryMonth,
                    ExpiryYear = card.ExpiryYear
                },
                IdempotencyKeyFor(order),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // PaymentService unreachable even after the HTTP resilience pipeline: the saga keeps the step and
            // the recovery worker retries later with the SAME Idempotency-Key (no double charge possible).
            saga.RetryCount++;
            saga.LastError = Truncate(ex.Message, LastErrorMaxLength);
            saga.UpdatedAtUtc = Now();

            if (saga.RetryCount >= settings.MaxPaymentAttempts)
            {
                await FailPaymentAndCompensateAsync(saga,
                    $"PaymentService unavailable after {saga.RetryCount} attempt(s): {ex.Message}",
                    "payment_service_unavailable", paymentId: null, cancellationToken);
                return;
            }

            AddStep(saga, SagaSteps.ProcessPayment, SagaStepOutcomes.Retrying,
                $"PaymentService unavailable (attempt {saga.RetryCount}/{settings.MaxPaymentAttempts}): {ex.Message}. " +
                $"Retrying in ~{settings.PaymentRetryIntervalSeconds}s with the same Idempotency-Key");
            await TrySaveAsync(saga, cancellationToken);
            return;
        }

        if (outcome.IsApproved)
        {
            await CompleteSagaAsync(saga, outcome.Payment!, outcome.Replayed, cancellationToken);
        }
        else
        {
            await FailPaymentAndCompensateAsync(saga, outcome.DeclineReason ?? "Payment declined", outcome.DeclineCode,
                outcome.PaymentId, cancellationToken);
        }
    }

    private async Task CompleteSagaAsync(SagaState saga, PaymentDto payment, bool replayed, CancellationToken cancellationToken)
    {
        var order = saga.Order;
        var now = Now();

        saga.PaymentProcessed = true;
        saga.PaymentId = payment.PaymentId;
        saga.ProtectedPaymentDetails = null; // data minimisation: card data no longer needed
        saga.Status = SagaStatuses.Completed;
        saga.CurrentStep = SagaSteps.Done;
        saga.LastError = null;
        saga.CompletedAtUtc = now;
        saga.UpdatedAtUtc = now;
        order.Status = OrderStatuses.Completed;
        order.PaymentId = payment.PaymentId;
        order.FailureReason = null;
        order.UpdatedAtUtc = now;

        AddStep(saga, SagaSteps.ProcessPayment, SagaStepOutcomes.Succeeded,
            $"Payment {payment.PaymentId} captured: {payment.Amount:F2} {payment.Currency} ({payment.CardBrand} ****{payment.CardLast4})" +
            (replayed ? " [idempotent replay]" : string.Empty));
        AddStep(saga, SagaSteps.CompleteOrder, SagaStepOutcomes.Succeeded,
            "Order completed; PaymentProcessedEvent queued so InventoryService commits the reserved stock");

        Enqueue(new PaymentProcessedEvent
        {
            CorrelationId = saga.CorrelationId,
            OrderId = order.OrderId,
            PaymentId = payment.PaymentId,
            Amount = payment.Amount,
            Currency = payment.Currency
        }, RoutingKeys.PaymentProcessed);

        if (await TrySaveAsync(saga, cancellationToken))
        {
            logger.LogInformation("SAGA {SagaId} COMPLETED: order {OrderNumber} paid with payment {PaymentId}",
                saga.SagaId, order.OrderNumber, payment.PaymentId);
        }
    }

    /// <summary>
    /// COMPENSATION: payment failed after stock was reserved. The order is cancelled and a
    /// <see cref="PaymentFailedEvent"/> is emitted - InventoryService answers it by running ReleaseStock.
    /// </summary>
    private async Task FailPaymentAndCompensateAsync(
        SagaState saga,
        string reason,
        string? declineCode,
        Guid? paymentId,
        CancellationToken cancellationToken)
    {
        var order = saga.Order;
        var now = Now();

        saga.ProtectedPaymentDetails = null;
        saga.PaymentId = paymentId;
        saga.Status = SagaStatuses.Compensating;
        saga.CurrentStep = SagaSteps.ReleaseStock;
        saga.LastError = Truncate(reason, LastErrorMaxLength);
        saga.RetryCount = 0;
        saga.UpdatedAtUtc = now;
        order.Status = OrderStatuses.Cancelled;
        order.FailureReason = Truncate($"Payment failed: {reason}", FailureReasonMaxLength);
        order.PaymentId = paymentId;
        order.UpdatedAtUtc = now;

        AddStep(saga, SagaSteps.ProcessPayment, SagaStepOutcomes.Failed,
            declineCode is null ? reason : $"{reason} [{declineCode}]");
        AddStep(saga, SagaSteps.ReleaseStock, SagaStepOutcomes.Compensating,
            "COMPENSATION started: PaymentFailedEvent queued - InventoryService will release the reserved stock");

        Enqueue(new PaymentFailedEvent
        {
            CorrelationId = saga.CorrelationId,
            OrderId = order.OrderId,
            PaymentId = paymentId,
            Amount = order.TotalAmount,
            Currency = order.Currency,
            Reason = reason,
            DeclineCode = declineCode
        }, RoutingKeys.PaymentFailed);

        if (await TrySaveAsync(saga, cancellationToken))
        {
            logger.LogWarning("SAGA {SagaId} COMPENSATING: payment for order {OrderNumber} failed ({Reason}); order Cancelled, releasing stock",
                saga.SagaId, order.OrderNumber, reason);
        }
    }

    // =====================================================================================================
    // Recovery (driven by SagaRecoveryService)
    // =====================================================================================================

    public async Task<IReadOnlyList<StalledSaga>> FindStalledSagasAsync(int maxCount, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = Now();
        var paymentCutoff = now.AddSeconds(-settings.PaymentRetryIntervalSeconds);
        var reservationCutoff = now.AddSeconds(-settings.StockReservationTimeoutSeconds);
        var compensationCutoff = now.AddSeconds(-settings.CompensationRetryIntervalSeconds);

        return await db.SagaStates
            .AsNoTracking()
            .Where(s =>
                (s.Status == SagaStatuses.Running && s.CurrentStep == SagaSteps.ProcessPayment && s.UpdatedAtUtc <= paymentCutoff) ||
                (s.Status == SagaStatuses.Running && s.CurrentStep == SagaSteps.ReserveStock && s.UpdatedAtUtc <= reservationCutoff) ||
                (s.Status == SagaStatuses.Compensating && s.UpdatedAtUtc <= compensationCutoff))
            .OrderBy(s => s.UpdatedAtUtc)
            .Take(maxCount)
            .Select(s => new StalledSaga(s.SagaId, s.OrderId, s.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task ResumeAsync(Guid sagaId, CancellationToken cancellationToken)
    {
        var saga = await db.SagaStates
            .Include(s => s.Order).ThenInclude(o => o.OrderItems)
            .SingleOrDefaultAsync(s => s.SagaId == sagaId, cancellationToken);
        if (saga is null)
        {
            return;
        }

        var settings = options.Value;
        var order = saga.Order;
        var now = Now();

        // (a) Payment step stuck (crash mid-call) or PaymentService was unavailable: retry it.
        if (saga.Status == SagaStatuses.Running && saga.CurrentStep == SagaSteps.ProcessPayment
            && saga.UpdatedAtUtc <= now.AddSeconds(-settings.PaymentRetryIntervalSeconds))
        {
            saga.UpdatedAtUtc = now;
            AddStep(saga, SagaSteps.ProcessPayment, SagaStepOutcomes.Retrying,
                $"Recovery worker resumes the payment step (attempt {saga.RetryCount + 1}/{settings.MaxPaymentAttempts}, same Idempotency-Key)");
            if (!await TrySaveAsync(saga, cancellationToken))
            {
                return;
            }

            await ExecutePaymentStepAsync(saga, cancellationToken);
            return;
        }

        // (b) InventoryService never answered: cancel and release whatever might have been reserved.
        if (saga.Status == SagaStatuses.Running && saga.CurrentStep == SagaSteps.ReserveStock
            && saga.UpdatedAtUtc <= now.AddSeconds(-settings.StockReservationTimeoutSeconds))
        {
            var reason = $"InventoryService did not answer within {settings.StockReservationTimeoutSeconds}s";
            saga.Status = SagaStatuses.Compensating;
            saga.CurrentStep = SagaSteps.ReleaseStock;
            saga.LastError = reason;
            saga.RetryCount = 0;
            saga.ProtectedPaymentDetails = null;
            saga.UpdatedAtUtc = now;
            order.Status = OrderStatuses.Cancelled;
            order.FailureReason = $"Cancelled: {reason}";
            order.UpdatedAtUtc = now;

            AddStep(saga, SagaSteps.ReserveStock, SagaStepOutcomes.Failed, reason);
            AddStep(saga, SagaSteps.ReleaseStock, SagaStepOutcomes.Compensating,
                "OrderCancelledEvent queued so InventoryService releases any reservation it may hold");
            Enqueue(new OrderCancelledEvent
            {
                CorrelationId = saga.CorrelationId,
                OrderId = order.OrderId,
                Reason = reason
            }, RoutingKeys.OrderCancelled);

            if (await TrySaveAsync(saga, cancellationToken))
            {
                logger.LogWarning("SAGA {SagaId} TIMED OUT waiting for stock reservation of order {OrderNumber}; compensating",
                    saga.SagaId, order.OrderNumber);
            }

            return;
        }

        // (c) Compensation sent but never confirmed: re-send (ReleaseStock is idempotent) or give up.
        if (saga.Status == SagaStatuses.Compensating
            && saga.UpdatedAtUtc <= now.AddSeconds(-settings.CompensationRetryIntervalSeconds))
        {
            saga.UpdatedAtUtc = now;

            if (saga.RetryCount + 1 >= settings.MaxCompensationAttempts)
            {
                saga.Status = SagaStatuses.Failed;
                saga.LastError = "Compensation (ReleaseStock) was never confirmed - manual intervention required";
                AddStep(saga, SagaSteps.ReleaseStock, SagaStepOutcomes.Failed,
                    $"No StockReleasedEvent after {saga.RetryCount + 1} attempt(s); manual intervention required");
                if (await TrySaveAsync(saga, cancellationToken))
                {
                    logger.LogError("SAGA {SagaId}: compensation for order {OrderNumber} not confirmed - manual intervention required",
                        saga.SagaId, order.OrderNumber);
                }

                return;
            }

            saga.RetryCount++;
            AddStep(saga, SagaSteps.ReleaseStock, SagaStepOutcomes.Retrying,
                $"No StockReleasedEvent yet; re-sending the compensation (attempt {saga.RetryCount + 1}/{settings.MaxCompensationAttempts})");
            Enqueue(new OrderCancelledEvent
            {
                CorrelationId = saga.CorrelationId,
                OrderId = order.OrderId,
                Reason = order.FailureReason ?? "Compensation retry"
            }, RoutingKeys.OrderCancelled);
            await TrySaveAsync(saga, cancellationToken);
        }
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    public static string IdempotencyKeyFor(Order order) => $"order-{order.OrderId:N}-charge";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

    private Task<SagaState?> LoadSagaAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.SagaStates
            .Include(s => s.Order).ThenInclude(o => o.OrderItems)
            .SingleOrDefaultAsync(s => s.OrderId == orderId, cancellationToken);

    /// <summary>UTC "now" truncated to milliseconds (the precision of the datetime2(3) columns).</summary>
    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private void AddStep(SagaState saga, string stepName, string outcome, string? detail)
    {
        // Strictly increasing timestamps keep the audit trail in order even within one millisecond.
        var occurredAt = Now();
        if (occurredAt <= _lastStepTimestamp)
        {
            occurredAt = _lastStepTimestamp.AddMilliseconds(1);
        }

        _lastStepTimestamp = occurredAt;

        saga.SagaStepLogs.Add(new SagaStepLog
        {
            SagaId = saga.SagaId,
            StepName = stepName,
            Outcome = outcome,
            Detail = detail is null ? null : Truncate(detail, StepDetailMaxLength),
            OccurredAtUtc = occurredAt
        });

        logger.LogInformation("SAGA {SagaId} [{Step}] {Outcome}: {Detail}", saga.SagaId, stepName, outcome, detail);
    }

    private void Enqueue(IntegrationEvent integrationEvent, string routingKey)
    {
        db.OutboxMessages.Add(OutboxMessageFactory.Create(integrationEvent, routingKey, Now()));
        _outboxPending = true;
    }

    private void NotifyOutbox()
    {
        _outboxPending = false;
        outboxSignal.Notify();
    }

    /// <summary>Saves a transition; returns false (and discards it) when another worker changed the saga first.</summary>
    private async Task<bool> TrySaveAsync(SagaState saga, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (_outboxPending)
            {
                NotifyOutbox();
            }

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("SAGA {SagaId}: state was changed concurrently by another worker - transition discarded",
                saga.SagaId);
            db.ChangeTracker.Clear();
            _outboxPending = false;
            return false;
        }
    }

    private static string Describe(IReadOnlyCollection<ReservedStockLine> lines) =>
        lines.Count == 0 ? "nothing" : string.Join(", ", lines.Select(l => $"{l.Quantity} x product {l.ProductId}"));

    private static string Describe(IReadOnlyCollection<ReleasedStockLine> lines) =>
        lines.Count == 0 ? "nothing (no active reservation)" : string.Join(", ", lines.Select(l => $"{l.Quantity} x product {l.ProductId}"));
}
