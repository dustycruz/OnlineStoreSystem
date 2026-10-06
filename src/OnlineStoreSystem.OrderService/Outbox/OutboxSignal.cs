// File: src/OnlineStoreSystem.OrderService/Outbox/OutboxSignal.cs
namespace OnlineStoreSystem.OrderService.Outbox;

/// <summary>
/// In-process "doorbell" that wakes the <see cref="OutboxDispatcher"/> right after a transaction that wrote
/// outbox rows committed, so events are relayed within milliseconds instead of waiting for the next poll.
/// Losing a signal is harmless: the dispatcher also polls.
/// </summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        if (_semaphore.CurrentCount > 0)
        {
            return;
        }

        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another thread rang the bell concurrently - nothing to do.
        }
    }

    /// <summary>Waits for a signal or the timeout, whichever comes first.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);
}
