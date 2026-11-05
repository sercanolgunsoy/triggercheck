using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace TriggerCheck.Infrastructure;

public enum DatabaseProvider { Sqlite }

/// <summary>
/// Database provider configuration. Each provider has its own migrations assembly (EF migrations are provider
/// specific): <c>TriggerCheck.Migrations.Sqlite</c>. The host must reference it (TriggerCheck.Web does), because
/// EF loads it by name.
/// </summary>
public static class DatabaseOptions
{
    public const string SqliteMigrationsAssembly = "TriggerCheck.Migrations.Sqlite";
    public const string DefaultSqliteConnectionString = "Data Source=triggercheck.db";

    public static DatabaseProvider GetProvider(IConfiguration config) => DatabaseProvider.Sqlite;

    public static string GetConnectionString(IConfiguration config) =>
        config.GetConnectionString("Default") ?? DefaultSqliteConnectionString;

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, DatabaseProvider provider, string connectionString) =>
        provider switch
        {
            _ => options.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly)),
        };
}
