using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TriggerCheck.Infrastructure.Data;

/// <summary>
/// PostgreSQL only (OPS-2): every transaction EF starts first takes one cluster-wide transaction-level advisory lock.
/// <para>
/// The code was written for SQLite, where Microsoft.Data.Sqlite starts transactions with <c>BEGIN IMMEDIATE</c>: the
/// write lock is taken up front, so read-check-write transactions (plan quotas, AI budget reservations, the monthly
/// report claim, incident group decisions...) run one at a time. Postgres' default READ COMMITTED would let two of them
/// pass the same check. Taking the same advisory lock at the start of each transaction restores the SQLite semantics
/// without touching every call site. Single statements outside a transaction (the scheduler's compare-and-set leases,
/// <c>ExecuteUpdate</c>, single-row <c>SaveChanges</c>) do not take it; Postgres re-evaluates their WHERE clause on a
/// concurrently updated row, so the compare-and-set stays correct.
/// </para>
/// <para>
/// Transactions asked for as SERIALIZABLE / REPEATABLE READ (meaning "BEGIN IMMEDIATE" on SQLite) run as READ COMMITTED:
/// a snapshot taken before the lock wait would not see the previous holder's commit, and the lock already serializes
/// them. The lock is released at commit / rollback. Waiting is bounded like SQLite's busy timeout: <c>lock_timeout</c>
/// = the context's command timeout (default 30 s), then LockNotAvailable (<see cref="DbErrors.IsBusy"/>). A narrower
/// lock (e.g. per customer) would scale better and can replace this once the call sites are audited for it.
/// </para>
/// </summary>
public sealed class PostgresWriteLockInterceptor : DbTransactionInterceptor
{
    public static readonly PostgresWriteLockInterceptor Instance = new();

    /// <summary>Advisory lock key ("TrgC").</summary>
    public const long LockKey = 0x54726743;

    private const int DefaultTimeoutSeconds = 30;

    private PostgresWriteLockInterceptor() { }

    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result) =>
        !result.HasResult && Snapshot(eventData.IsolationLevel) ? InterceptionResult<DbTransaction>.SuppressWithResult(connection.BeginTransaction(IsolationLevel.ReadCommitted)) : result;

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
        TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default) =>
        !result.HasResult && Snapshot(eventData.IsolationLevel)
            ? InterceptionResult<DbTransaction>.SuppressWithResult(await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            : result;

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = Command(connection, result, eventData.Context);
        command.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, result, eventData.Context);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    private static bool Snapshot(IsolationLevel level) => level is IsolationLevel.Serializable or IsolationLevel.RepeatableRead or IsolationLevel.Snapshot;

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, DbContext? context)
    {
        var seconds = Math.Clamp(context?.Database.GetCommandTimeout() ?? DefaultTimeoutSeconds, 1, 3600);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = seconds + 5; // lock_timeout fires first (clean LockNotAvailable error)
        command.CommandText = $"SET LOCAL lock_timeout = '{seconds}s'; SELECT pg_advisory_xact_lock({LockKey});";
        return command;
    }
}
