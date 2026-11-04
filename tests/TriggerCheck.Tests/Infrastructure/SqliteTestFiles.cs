using Microsoft.Data.Sqlite;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>
/// Pool handling for the per-test SQLite files. Never call <see cref="SqliteConnection.ClearAllPools"/> in tests: it is
/// process-wide and disposes connections that other test classes (running in parallel) are using at that moment
/// (ObjectDisposedException 'SQLitePCL.sqlite3' in their queries, seen as random 500s / lost writes).
/// </summary>
public static class SqliteTestFiles
{
    /// <summary>Closes the pooled connections of this database only (other tests' pools are left alone).</summary>
    public static void ClearPool(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>Clears this database's pool, then deletes the file and its WAL / shared-memory files (best effort).</summary>
    public static void Delete(string path)
    {
        ClearPool($"Data Source={path}");
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(file); } catch { /* ignore */ }
    }
}
