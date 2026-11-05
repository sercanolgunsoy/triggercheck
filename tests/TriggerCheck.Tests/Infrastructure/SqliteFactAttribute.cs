namespace TriggerCheck.Tests.Infrastructure;

/// <summary>A test of SQLite-only behaviour (WAL mode, file handling): skipped in a Postgres test run (OPS-2).</summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (TestDatabase.UsePostgres) Skip = "SQLite only (TRIGGERCHECK_TEST_DB=postgres)";
    }
}
