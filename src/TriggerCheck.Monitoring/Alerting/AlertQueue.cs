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
/// Bounded in-process queue (<see cref="Channel{T}"/>) between the check runner and <see cref="AlertSenderService"/>,
/// so a slow or hanging notification target never holds a check slot.
/// Known limitation: the queue lives in memory; alerts still queued or being retried when the process crashes are lost
/// (a graceful shutdown drains it, see <see cref="AlertSenderService"/>).
/// </summary>
public sealed class AlertQueue : IAlertQueue
{
    private readonly Channel<AlertMessage> _channel;
    private readonly ILogger<AlertQueue> _logger;
    private int _pending;

    public AlertQueue(IOptions<AlertingOptions> options, ILogger<AlertQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<AlertMessage>(new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false instead of waiting
            SingleReader = true
        });
    }

    public ChannelReader<AlertMessage> Reader => _channel.Reader;

    /// <summary>Alerts queued or being delivered.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public bool TryEnqueue(AlertMessage message)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(message)) return true;
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
