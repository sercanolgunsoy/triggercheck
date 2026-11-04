using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default") ?? "Data Source=triggercheck.db";
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(cs).AddInterceptors(SqliteWalInterceptor.Instance));
        // Monitoring engine runs many short-lived, parallel units of work -> factory.
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(cs).AddInterceptors(SqliteWalInterceptor.Instance), ServiceLifetime.Scoped);
        return services;
    }
}

/// <summary>
/// Switches SQLite databases to WAL journal mode when a connection is opened, so the scheduler's writes do not
/// block UI reads (and vice versa). The mode is persistent in the file; the pragma is a cheap no-op afterwards.
/// Only acts on SQLite connections (in-memory databases silently keep their "memory" mode).
/// </summary>
public sealed class SqliteWalInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteWalInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is not SqliteConnection) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection) return;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
