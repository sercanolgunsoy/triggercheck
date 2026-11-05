using Microsoft.Data.Sqlite;
using Npgsql;

namespace TriggerCheck.Infrastructure.Data;

/// <summary>Provider-neutral classification of database errors (SQLite and PostgreSQL, OPS-2).</summary>
public static class DbErrors
{
    /// <summary>A unique / constraint violation (SQLITE_CONSTRAINT 19, Postgres 23505), possibly wrapped in a DbUpdateException.</summary>
    public static bool IsUniqueViolation(Exception ex) =>
        Find<SqliteException>(ex) is { SqliteErrorCode: 19 }
        || Find<PostgresException>(ex) is { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// The write lock could not be obtained in time: SQLITE_BUSY (5) / SQLITE_LOCKED (6); on Postgres a lock timeout,
    /// deadlock or serialization failure (the transaction can simply be retried).
    /// </summary>
    public static bool IsBusy(Exception ex) =>
        Find<SqliteException>(ex) is { SqliteErrorCode: 5 or 6 }
        || Find<PostgresException>(ex) is { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected };

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is T match) return match;
        return null;
    }
}
