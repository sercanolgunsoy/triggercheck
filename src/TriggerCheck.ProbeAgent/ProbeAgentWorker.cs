using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Regions;

namespace TriggerCheck.ProbeAgent;

/// <summary>
/// The agent loop. Every <see cref="AgentOptions.PollIntervalSeconds"/> it leases as many due probes as it has free slots
/// (<see cref="AgentOptions.MaxConcurrency"/>), runs each with the shared checkers (same TargetGuard SSRF rules as the
/// main app; Monitoring:AllowPrivateTargets is off unless explicitly set) and buffers the outcomes; a flusher posts them
/// in batches about twice a second, so every result arrives well within its lease. Results that could not be posted
/// are retried until they are older than <see cref="MaxResultAge"/> (the central side rejects expired leases anyway).
/// Errors back off exponentially (max 60 s); a rejected credential is logged without the credential.
/// </summary>
public sealed class ProbeAgentWorker(
    AgentApiClient api,
    IEnumerable<IMonitorChecker> checkers,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<ProbeAgentWorker> logger) : BackgroundService
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MaxResultAge = TimeSpan.FromMinutes(5);

    private readonly Dictionary<MonitorType, IMonitorChecker> _checkers = checkers.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.First());
    private readonly Channel<ProbeResult> _results = Channel.CreateUnbounded<ProbeResult>();
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private int _inFlight;

    public int InFlight => Volatile.Read(ref _inFlight);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        logger.LogInformation("Probe agent for region {Region} started (central {Central}, concurrency {Max})", o.RegionId, o.CentralUrl, o.MaxConcurrency);
        var flusher = Task.Run(() => FlushLoopAsync(stoppingToken), CancellationToken.None);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await LeaseAndStartAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentRejectedException ex)
            {
                failures++;
                logger.LogError("{Message}", ex.Message);
            }
            catch (Exception ex)
            {
                failures++;
                logger.LogWarning("Lease request failed: {Error}", ex.Message);
            }

            var poll = Math.Max(1, o.PollIntervalSeconds);
            var delay = TimeSpan.FromSeconds(failures == 0 ? poll : Math.Min(60, poll * Math.Pow(2, Math.Min(failures, 6))));
            try { await Task.Delay(delay, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Shutdown: let running probes finish (bounded) and post what we have.
        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { /* don't block shutdown */ }
        try { await FlushAsync(CancellationToken.None); } catch (Exception ex) { logger.LogWarning("Final flush failed: {Error}", ex.Message); }
        await flusher;
        logger.LogInformation("Probe agent stopped");
    }

    /// <summary>One poll: leases up to the free slots and starts the probes (not awaited). Returns the number leased.</summary>
    public async Task<int> LeaseAndStartAsync(CancellationToken ct)
    {
        var free = Math.Max(1, options.Value.MaxConcurrency) - InFlight;
        if (free <= 0) return 0;
        var lease = await api.LeaseAsync(Math.Min(free, AgentProtocol.MaxLeaseItems), ct);
        foreach (var item in lease.Items)
        {
            Interlocked.Increment(ref _inFlight);
            var task = Task.Run(() => RunItemAsync(item, ct), CancellationToken.None);
            _running.TryAdd(task, 0);
            _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
        }
        return lease.Items.Count;
    }

    /// <summary>Completes when every probe started so far has finished.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Keys);

    private async Task RunItemAsync(LeaseItem item, CancellationToken ct)
    {
        try
        {
            var checkedAt = time.GetUtcNow().UtcDateTime;
            var outcome = await ProbeAsync(item, ct);
            _results.Writer.TryWrite(AgentProtocol.ToResult(item.ItemId, outcome, checkedAt));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down: the lease expires and the probe is leased again
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<CheckOutcome> ProbeAsync(LeaseItem item, CancellationToken ct)
    {
        if (!_checkers.TryGetValue(item.Type, out var checker)) return CheckOutcome.Down($"Monitor type {item.Type} is not supported by the probe agent");
        try
        {
            return await checker.CheckAsync(AgentProtocol.ToMonitor(item), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Checker for {MonitorType} threw", item.Type);
            return CheckOutcome.Down("Internal checker error");
        }
    }

    /// <summary>Posts every buffered result (in batches). Returns the number of results the central side acknowledged.</summary>
    public async Task<int> FlushAsync(CancellationToken ct)
    {
        await _flushLock.WaitAsync(ct);
        try
        {
            var buffered = new List<ProbeResult>();
            while (_results.Reader.TryRead(out var r)) buffered.Add(r);
            var cutoff = time.GetUtcNow().UtcDateTime - MaxResultAge;
            buffered.RemoveAll(r => r.CheckedAtUtc < cutoff);
            var posted = 0;
            for (var i = 0; i < buffered.Count; i += AgentProtocol.MaxResultsPerRequest)
            {
                var batch = buffered.Skip(i).Take(AgentProtocol.MaxResultsPerRequest).ToList();
                try
                {
                    var response = await api.PostResultsAsync(batch, ct);
                    posted += batch.Count;
                    foreach (var ack in response.Results.Where(a => a.Status is not (ResultStatus.Accepted or ResultStatus.Duplicate)))
                        logger.LogInformation("Result {ItemId} not applied: {Status}", ack.ItemId, ack.Status);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Keep this and the remaining results for the next flush.
                    foreach (var r in buffered.Skip(i)) _results.Writer.TryWrite(r);
                    if (ex is AgentRejectedException) logger.LogError("{Message}", ex.Message);
                    else logger.LogWarning("Posting results failed: {Error}", ex.Message);
                    break;
                }
            }
            return posted;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task FlushLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, time, stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Result flush failed: {Error}", ex.Message);
            }
        }
    }
}
