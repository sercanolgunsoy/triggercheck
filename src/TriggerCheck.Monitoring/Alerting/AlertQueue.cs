using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Monitoring.Alerting;

/// <summary>Hands alerts from the check runner to the <see cref="AlertSenderService"/>. Never blocks.</summary>
public interface IAlertQueue
{
    /// <summary>Queues the alert for delivery. Returns false (and logs an error) when the queue is full or shut down.</summary>
    bool TryEnqueue(AlertMessage message);
}

/// <summary>
/// In-process queue (<see cref="Channel{T}"/>) between the check runner and <see cref="AlertSenderService"/>, so a
/// slow or hanging notification target never holds a check slot. Bounded by <see cref="AlertingOptions.QueueCapacity"/>
/// alerts that are queued or still in delivery (including retry waits).
/// Known limitation: the queue lives in memory; alerts still queued or being retried when the process crashes are lost
/// (a graceful shutdown drains it, see <see cref="AlertSenderService"/>).
/// </summary>
public sealed class AlertQueue : IAlertQueue
{
    private readonly Channel<AlertMessage> _channel = Channel.CreateUnbounded<AlertMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ILogger<AlertQueue> _logger;
    private readonly int _capacity;
    private int _pending;

    public AlertQueue(IOptions<AlertingOptions> options, ILogger<AlertQueue> logger)
    {
        _logger = logger;
        _capacity = Math.Max(1, options.Value.QueueCapacity);
    }

    public ChannelReader<AlertMessage> Reader => _channel.Reader;

    /// <summary>Alerts queued or being delivered.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public bool TryEnqueue(AlertMessage message)
    {
        if (Interlocked.Increment(ref _pending) <= _capacity && _channel.Writer.TryWrite(message)) return true;
        Interlocked.Decrement(ref _pending);
        _logger.LogError("Alert queue full or shut down; alert '{Subject}' for monitor {MonitorId} was dropped",
            message.Subject, message.MonitorId);
        return false;
    }

    /// <summary>Called by the sender once an alert has been fully handled (delivered, failed or dropped).</summary>
    internal void MarkDone() => Interlocked.Decrement(ref _pending);

    /// <summary>No more alerts are accepted; the reader finishes once the queued ones are read.</summary>
    internal void Complete() => _channel.Writer.TryComplete();

    /// <summary>Completes when nothing is queued or in delivery (tests / diagnostics).</summary>
    public async Task WhenIdleAsync(CancellationToken ct = default)
    {
        while (Pending > 0) await Task.Delay(10, ct);
    }
}

/// <summary>
/// Limits concurrent delivery attempts: <see cref="AlertingOptions.MaxConcurrency"/> overall and
/// <see cref="AlertingOptions.MaxConcurrencyPerCustomer"/> per customer. Held only for one attempt, never during retry
/// waits. The customer slot is taken first, so a customer waiting for its own cap does not hold a global slot.
/// (Per-customer semaphores are kept for the process lifetime: one small object per customer that ever alerted.)
/// </summary>
public sealed class AlertDeliveryThrottle(IOptions<AlertingOptions> options)
{
    private readonly SemaphoreSlim _global = new(Math.Max(1, options.Value.MaxConcurrency));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _perCustomer = new();
    private readonly int _perCustomerLimit = Math.Max(1, options.Value.MaxConcurrencyPerCustomer);

    public async Task<IDisposable> AcquireAsync(int customerId, CancellationToken ct)
    {
        var customer = _perCustomer.GetOrAdd(customerId, _ => new SemaphoreSlim(_perCustomerLimit));
        await customer.WaitAsync(ct);
        try
        {
            await _global.WaitAsync(ct);
        }
        catch
        {
            customer.Release();
            throw;
        }
        return new Releaser(this, customer);
    }

    private sealed class Releaser(AlertDeliveryThrottle owner, SemaphoreSlim customer) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            owner._global.Release();
            customer.Release();
        }
    }
}
