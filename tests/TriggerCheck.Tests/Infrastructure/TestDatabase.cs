using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>A fresh, empty database for one test (a temp SQLite file), deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _path;

    private TestDatabase(string prefix)
    {
        _path = Path.Combine(Path.GetTempPath(), $"triggercheck-{prefix}-{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={_path}";
    }

    public DatabaseProvider Provider => DatabaseProvider.Sqlite;
    public string ConnectionString { get; }

    public static TestDatabase Create(string prefix) => new(prefix);

    public AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseOptions.Configure(options, Provider, ConnectionString);
        return new AppDbContext(options.Options);
    }

    public void Dispose() => SqliteTestFiles.Delete(_path);
}
