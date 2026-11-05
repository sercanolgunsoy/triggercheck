using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var provider = DatabaseOptions.GetProvider(config);
        var cs = DatabaseOptions.GetConnectionString(config);
        services.AddDbContext<AppDbContext>(o => DatabaseOptions.Configure(o, provider, cs));
        // Monitoring engine runs many short-lived, parallel units of work -> factory.
        services.AddDbContextFactory<AppDbContext>(o => DatabaseOptions.Configure(o, provider, cs), ServiceLifetime.Scoped);
        return services;
    }

    /// <summary>
    /// Switches a SQLite database file to WAL journal mode, so the scheduler's writes do not block UI reads (and
    /// vice versa). The mode is persistent in the file, so this runs once at startup (after the schema is created).
    /// No-op for other providers; in-memory SQLite databases keep their "memory" mode.
    /// </summary>
    public static async Task EnableSqliteWalAsync(this AppDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite()) return;
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
