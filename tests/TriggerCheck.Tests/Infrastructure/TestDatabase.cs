using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>
/// A fresh database for one test host (OPS-2). By default a temp SQLite file; with the environment variable
/// <c>TRIGGERCHECK_TEST_DB=postgres</c> a new PostgreSQL database on the server in <c>TRIGGERCHECK_TEST_PG</c> (admin
/// connection string, default <c>Host=localhost;Username=postgres;Password=postgres</c>), dropped on dispose. See
/// .github/workflows/ci.yml (job <c>test-postgres</c>).
/// <para>
/// Postgres databases with schema are copied from a template that is migrated once per test run (CREATE DATABASE ...
/// TEMPLATE), which is much faster than creating ~45 tables per host; it also runs the Postgres migrations in every run.
/// </para>
/// </summary>
public sealed class TestDatabase : IDisposable
{
    public static bool UsePostgres { get; } =
        string.Equals(Environment.GetEnvironmentVariable("TRIGGERCHECK_TEST_DB"), "postgres", StringComparison.OrdinalIgnoreCase);

    private readonly string? _sqlitePath;
    private readonly string? _postgresName;

    private TestDatabase(string? sqlitePath, string? postgresName, string connectionString)
    {
        _sqlitePath = sqlitePath;
        _postgresName = postgresName;
        ConnectionString = connectionString;
    }

    public DatabaseProvider Provider => _postgresName is null ? DatabaseProvider.Sqlite : DatabaseProvider.Postgres;
    public string ConnectionString { get; }

    /// <summary>Settings for a host on this database (<c>Database:Provider</c>, <c>ConnectionStrings:Default</c>).</summary>
    public IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        new("Database:Provider", Provider.ToString()),
        new("ConnectionStrings:Default", ConnectionString),
    ];

    /// <summary>
    /// <paramref name="withSchema"/>: Postgres only, the database is created with the migrated schema (SQLite files start
    /// empty and get their schema from the host, as before). <paramref name="sqliteOptions"/> is appended to the SQLite
    /// connection string (e.g. <c>Pooling=False</c>).
    /// </summary>
    public static TestDatabase Create(string prefix, bool withSchema = true, string? sqliteOptions = null)
    {
        if (UsePostgres)
        {
            var name = PostgresTestServer.CreateDatabase(withSchema);
            return new TestDatabase(null, name, PostgresTestServer.ConnectionString(name));
        }
        var path = Path.Combine(Path.GetTempPath(), $"triggercheck-{prefix}-{Guid.NewGuid():N}.db");
        return new TestDatabase(path, null, $"Data Source={path}" + (sqliteOptions is null ? "" : ";" + sqliteOptions));
    }

    public AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseOptions.Configure(options, Provider, ConnectionString);
        return new AppDbContext(options.Options);
    }

    /// <summary>
    /// Makes the host's database unusable from now on: SQLite deletes the file (<paramref name="replaceWithGarbage"/>:
    /// and puts a non-database file in its place, so every query fails), Postgres drops the database.
    /// </summary>
    public static void Destroy(IConfiguration config, bool replaceWithGarbage = true)
    {
        var cs = config.GetConnectionString("Default")!;
        if (DatabaseOptions.GetProvider(config) == DatabaseProvider.Postgres)
        {
            PostgresTestServer.DropDatabase(new NpgsqlConnectionStringBuilder(cs).Database!);
            return;
        }
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs).DataSource;
        SqliteTestFiles.ClearPool($"Data Source={path}"); // not ClearAllPools: other tests run in parallel
        File.Delete(path);
        if (replaceWithGarbage) File.WriteAllBytes(path, [1, 2, 3]); // not even a database
    }

    public void Dispose()
    {
        if (_sqlitePath is not null) SqliteTestFiles.Delete(_sqlitePath);
        if (_postgresName is not null) PostgresTestServer.DropDatabase(_postgresName);
    }
}

/// <summary>The PostgreSQL server of a <c>TRIGGERCHECK_TEST_DB=postgres</c> test run.</summary>
internal static class PostgresTestServer
{
    private static readonly string AdminConnectionString = new NpgsqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TRIGGERCHECK_TEST_PG") ?? "Host=localhost;Username=postgres;Password=postgres")
    { Pooling = false }.ConnectionString;

    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly Lazy<string> Template = new(CreateTemplate);
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database, Pooling = true }.ConnectionString;

    public static string CreateDatabase(bool withSchema)
    {
        var template = withSchema ? Template.Value : null;
        var name = $"tc_{RunId}_{Guid.NewGuid():N}"[..48];
        // One at a time: CREATE DATABASE ... TEMPLATE fails while another session uses the template.
        CreateGate.Wait();
        try
        {
            Execute(template is null ? $"CREATE DATABASE \"{name}\"" : $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"");
        }
        finally
        {
            CreateGate.Release();
        }
        return name;
    }

    public static void DropDatabase(string name)
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString(name)));
        Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    private static string CreateTemplate()
    {
        var name = $"tc_{RunId}_template";
        Execute($"CREATE DATABASE \"{name}\"");
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseOptions.Configure(options, DatabaseProvider.Postgres, ConnectionString(name));
        using (var db = new AppDbContext(options.Options))
            DatabaseMigrator.MigrateAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString(name))); // a template must have no sessions
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { DropDatabase(name); } catch { /* best effort */ }
        };
        return name;
    }

    private static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(AdminConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
