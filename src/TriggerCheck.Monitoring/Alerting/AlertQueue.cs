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
/// slow or hanging notification target never holds a check slot. Bounded by alerts that are queued or still in
/// delivery (including retry waits): <see cref="AlertingOptions.QueueCapacity"/> overall and
/// <see cref="AlertingOptions.QueueCapacityPerCustomer"/> per customer, so one tenant cannot fill the queue for all.
/// Known limitation: the queue lives in memory; alerts still queued or being retried when the process crashes are lost
/// (a graceful shutdown drains it, see <see cref="AlertSenderService"/>).
/// </summary>
public sealed class AlertQueue : IAlertQueue
{
    private readonly Channel<AlertMessage> _channel = Channel.CreateUnbounded<AlertMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ILogger<AlertQueue> _logger;
    private readonly int _capacity;
    private readonly int _perCustomerCapacity;
    private readonly object _lock = new();
    private readonly Dictionary<int, int> _pendingByCustomer = new();
    private int _pending;

    public AlertQueue(IOptions<AlertingOptions> options, ILogger<AlertQueue> logger)
    {
        _logger = logger;
        _capacity = Math.Max(1, options.Value.QueueCapacity);
        _perCustomerCapacity = Math.Max(1, options.Value.QueueCapacityPerCustomer);
    }

    public ChannelReader<AlertMessage> Reader => _channel.Reader;

    /// <summary>Alerts queued or being delivered.</summary>
    public int Pending
    {
        get { lock (_lock) return _pending; }
    }

    public bool TryEnqueue(AlertMessage message)
    {
        string? refused = null;
        lock (_lock)
        {
            _pendingByCustomer.TryGetValue(message.CustomerId, out var mine);
            if (_pending >= _capacity) refused = "queue full";
            else if (mine >= _perCustomerCapacity) refused = "customer's share of the queue is full";
            else if (!_channel.Writer.TryWrite(message)) refused = "queue shut down";
            else
            {
                _pending++;
                _pendingByCustomer[message.CustomerId] = mine + 1;
                return true;
            }
        }
        _logger.LogError("Alert '{Subject}' for monitor {MonitorId} of customer {CustomerId} was dropped: {Reason}",
            message.Subject, message.MonitorId, message.CustomerId, refused);
        return false;
    }

    /// <summary>Called by the sender once an alert has been fully handled (delivered, failed, superseded or dropped).</summary>
    internal void MarkDone(AlertMessage message)
    {
        lock (_lock)
        {
            _pending--;
            if (_pendingByCustomer.TryGetValue(message.CustomerId, out var mine))
            {
                if (mine <= 1) _pendingByCustomer.Remove(message.CustomerId);
                else _pendingByCustomer[message.CustomerId] = mine - 1;
            }
        }
    }

    /// <summary>No more alerts are accepted; the reader finishes once the queued ones are read.</summary>
    internal void Complete()
    {
        lock (_lock) _channel.Writer.TryComplete();
    }

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
/// A customer's semaphore is removed as soon as nobody holds or waits for it (no growth with deleted customers).
/// </summary>
public sealed class AlertDeliveryThrottle(IOptions<AlertingOptions> options)
{
    private readonly SemaphoreSlim _global = new(Math.Max(1, options.Value.MaxConcurrency));
    private readonly int _perCustomerLimit = Math.Max(1, options.Value.MaxConcurrencyPerCustomer);
    private readonly object _lock = new();
    private readonly Dictionary<int, CustomerGate> _perCustomer = new();

    private sealed class CustomerGate(int limit)
    {
        public readonly SemaphoreSlim Semaphore = new(limit);
        public int Users; // holders + waiters, guarded by the throttle's lock
    }

    /// <summary>Customers that currently have a semaphore (tests / diagnostics).</summary>
    public int CustomerCount
    {
        get { lock (_lock) return _perCustomer.Count; }
    }

    public async Task<IDisposable> AcquireAsync(int customerId, CancellationToken ct)
    {
        CustomerGate gate;
        lock (_lock)
        {
            if (!_perCustomer.TryGetValue(customerId, out gate!)) _perCustomer[customerId] = gate = new CustomerGate(_perCustomerLimit);
            gate.Users++;
        }
        try
        {
            await gate.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Leave(customerId, gate);
            throw;
        }
        try
        {
            await _global.WaitAsync(ct);
        }
        catch
        {
            gate.Semaphore.Release();
            Leave(customerId, gate);
            throw;
        }
        return new Releaser(this, customerId, gate);
    }

    private void Leave(int customerId, CustomerGate gate)
    {
        lock (_lock)
        {
            if (--gate.Users == 0) _perCustomer.Remove(customerId);
        }
    }

    private sealed class Releaser(AlertDeliveryThrottle owner, int customerId, CustomerGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            owner._global.Release();
            gate.Semaphore.Release();
            owner.Leave(customerId, gate);
        }
    }
}
