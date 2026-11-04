using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Ai.Execution;

/// <summary>Hands AI jobs to the background worker. Never blocks and never waits for the model.</summary>
public interface IAiJobQueue
{
    /// <summary>
    /// Queues the job. Returns false when the queue is full or shut down (logged; the feature simply has no AI output)
    /// or when the same job (feature + customer + subject) is already waiting.
    /// </summary>
    bool TryEnqueue(AiJob job);
}

/// <summary>
/// Bounded in-process queue (<see cref="Channel{T}"/>, <see cref="AiOptions.QueueCapacity"/>) drained by
/// <see cref="AiWorkerService"/>. Jobs for the same subject are coalesced while waiting; the identical input is not sent
/// twice (InputHash idempotency in <see cref="AiInterpreter"/>). Queued jobs are lost on a crash, which only means a
/// missing AI summary: the deterministic product is unaffected.
/// </summary>
public sealed class AiJobQueue : IAiJobQueue
{
    private readonly Channel<AiJob> _channel;
    private readonly ConcurrentDictionary<(string, int, string, string), byte> _waiting = new();
    private readonly ILogger<AiJobQueue> _logger;
    private int _pending;

    public AiJobQueue(IOptions<AiOptions> options, ILogger<AiJobQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<AiJob>(new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails instead of dropping silently
        });
    }

    public ChannelReader<AiJob> Reader => _channel.Reader;

    /// <summary>Jobs queued or running.</summary>
    public int Pending => Volatile.Read(ref _pending);

    private static (string, int, string, string) Key(AiJob j) => (j.Feature, j.CustomerId, j.SubjectType, j.SubjectId);

    public bool TryEnqueue(AiJob job)
    {
        if (!_waiting.TryAdd(Key(job), 0)) return false;
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(job)) return true;

        _waiting.TryRemove(Key(job), out _);
        Interlocked.Decrement(ref _pending);
        _logger.LogWarning("AI job {Feature} {SubjectType} {SubjectId} for customer {CustomerId} was dropped: queue full or shut down",
            job.Feature, job.SubjectType, job.SubjectId, job.CustomerId);
        return false;
    }

    /// <summary>Called by the worker when it takes the job (a new job for the subject may be queued from now on).</summary>
    internal void MarkStarted(AiJob job) => _waiting.TryRemove(Key(job), out _);

    internal void MarkDone() => Interlocked.Decrement(ref _pending);

    internal void Complete() => _channel.Writer.TryComplete();

    /// <summary>Completes when nothing is queued or running (tests / diagnostics).</summary>
    public async Task WhenIdleAsync(CancellationToken ct = default)
    {
        while (Pending > 0) await Task.Delay(10, ct);
    }
}

/// <summary>
/// Runs queued AI jobs with its own concurrency (<see cref="AiOptions.MaxConcurrency"/>), each in its own DI scope,
/// so AI calls never hold a request thread or a monitoring check slot. Always registered; when AI is off the jobs are
/// recorded as skipped without calling out.
/// </summary>
public sealed class AiWorkerService(AiJobQueue queue, IServiceScopeFactory scopes, IOptions<AiOptions> options, ILogger<AiWorkerService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, Math.Max(1, options.Value.MaxConcurrency)).Select(_ => RunWorkerAsync(stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(ct))
            {
                queue.MarkStarted(job);
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IAiInterpreter>().RunAsync(job, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AI job {Feature} {SubjectType} {SubjectId} failed", job.Feature, job.SubjectType, job.SubjectId);
                }
                finally
                {
                    queue.MarkDone();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown: queued jobs are dropped (no AI output is ever required)
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        return base.StopAsync(cancellationToken);
    }
}
