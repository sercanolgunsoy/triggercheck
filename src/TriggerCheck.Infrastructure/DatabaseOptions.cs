using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Infrastructure;

public enum DatabaseProvider { Sqlite, Postgres }

/// <summary>
/// Database provider configuration (OPS-2): <c>Database:Provider</c> = <c>Sqlite</c> (default) | <c>Postgres</c>, with
/// <c>ConnectionStrings:Default</c>. Each provider has its own migrations assembly (EF migrations are provider
/// specific): <c>TriggerCheck.Migrations.Sqlite</c> / <c>TriggerCheck.Migrations.Postgres</c>. The host must reference
/// both (TriggerCheck.Web does), because EF loads them by name.
/// </summary>
public static class DatabaseOptions
{
    public const string SqliteMigrationsAssembly = "TriggerCheck.Migrations.Sqlite";
    public const string PostgresMigrationsAssembly = "TriggerCheck.Migrations.Postgres";
    public const string DefaultSqliteConnectionString = "Data Source=triggercheck.db";

    public static DatabaseProvider GetProvider(IConfiguration config)
    {
        var value = config["Database:Provider"];
        if (string.IsNullOrWhiteSpace(value)) return DatabaseProvider.Sqlite;
        if (value.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) || value.Equals("Npgsql", StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.Postgres;
        return Enum.TryParse<DatabaseProvider>(value, ignoreCase: true, out var provider) && Enum.IsDefined(provider)
            ? provider
            : throw new InvalidOperationException($"Database:Provider '{value}' is not supported. Use Sqlite or Postgres.");
    }

    public static string GetConnectionString(IConfiguration config, DatabaseProvider provider)
    {
        var cs = config.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(cs)) return cs;
        return provider == DatabaseProvider.Sqlite
            ? DefaultSqliteConnectionString
            : throw new InvalidOperationException("ConnectionStrings:Default must be set for Database:Provider=Postgres.");
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, DatabaseProvider provider, string connectionString) =>
        provider switch
        {
            DatabaseProvider.Postgres => AddOnce(options.UseNpgsql(connectionString, o => o.MigrationsAssembly(PostgresMigrationsAssembly)),
                PostgresWriteLockInterceptor.Instance),
            _ => options.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly)),
        };

    // AddDbContext and AddDbContextFactory both run their configuration on the same options builder.
    private static DbContextOptionsBuilder AddOnce(DbContextOptionsBuilder options, IInterceptor interceptor) =>
        options.Options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(interceptor) == true ? options : options.AddInterceptors(interceptor);
}
