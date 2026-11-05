using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace TriggerCheck.Infrastructure.Data;

/// <summary>
/// Applies the EF Core migrations (OPS-1). A database created by <c>EnsureCreated</c> before migrations existed (tables
/// but no <c>__EFMigrationsHistory</c>) is baselined first: if it already has every table and column of the initial
/// migration, the initial migration is recorded as applied instead of being run; later migrations then run normally.
/// An older schema (missing tables / columns) is refused with an explanation, never altered. See docs/DEPLOYMENT.md
/// "Database and migrations".
/// </summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await BaselineAsync(db, logger, ct);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0) logger.LogInformation("Applying database migrations: {Migrations}", string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>For <c>Database:MigrateOnStartup=false</c>: fails startup if the schema is not up to date.</summary>
    public static async Task EnsureUpToDateAsync(AppDbContext db, CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
            throw new InvalidOperationException(
                $"The database schema is not up to date (pending migrations: {string.Join(", ", pending)}) and " +
                "Database:MigrateOnStartup is false. Run the migration step first: `dotnet TriggerCheck.Web.dll migrate` " +
                "(see docs/DEPLOYMENT.md, \"Database and migrations\").");
    }

    /// <summary>
    /// Records the initial migration as applied on a database created by <c>EnsureCreated</c>. Returns false when there
    /// is nothing to baseline (empty database, or the migration history already exists).
    /// </summary>
    public static async Task<bool> BaselineAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (!await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct)) return false;
        // The history table is looked up in the schema read below, not with IHistoryRepository.ExistsAsync: Npgsql 9.0.4
        // reports true there for a database without the table.
        var actual = await ReadSchemaAsync(db, ct);
        var historyTable = db.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault()?.MigrationsHistoryTableName
                           ?? HistoryRepository.DefaultTableName;
        if (actual.ContainsKey(historyTable)) return false;

        var migrations = db.GetService<IMigrationsAssembly>();
        var (initialId, initialType) = migrations.Migrations.First();
        var initial = migrations.CreateMigration(initialType, db.Database.ProviderName!);
        var expected = ExpectedSchema(db, initial);
        if (!expected.Keys.Any(actual.ContainsKey)) return false; // no TriggerCheck table: a normal (first) migration

        var history = db.GetService<IHistoryRepository>();

        var missing = new List<string>();
        foreach (var (table, columns) in expected)
        {
            if (!actual.TryGetValue(table, out var present)) missing.Add(table + " (table)");
            else missing.AddRange(columns.Where(c => !present.Contains(c)).Select(c => $"{table}.{c}"));
        }
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "The database has TriggerCheck tables but no migration history: it was created by EnsureCreated before " +
                $"EF migrations existed, and its schema is older than the initial migration ({initialId}). Missing: " +
                string.Join(", ", missing) + ". Nothing was changed. Back up the database, then either delete it (a new " +
                "one is created at the next start) or add the missing tables / columns by hand " +
                "(`dotnet ef migrations script` shows the SQL), and start again. See docs/DEPLOYMENT.md, \"Database and migrations\".");

        try
        {
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(initialId, ProductInfo.GetVersion())), ct);
        }
        catch (DbException)
        {
            // Another instance baselined the same database at the same moment: fine. Anything else is rethrown.
            if ((await history.GetAppliedMigrationsAsync(ct)).Any(r => r.MigrationId == initialId)) return false;
            throw;
        }
        logger.LogWarning("Database created by EnsureCreated (no migration history): its schema matches the initial migration " +
                          "{Migration}, which was recorded as applied (baseline).", initialId);
        return true;
    }

    /// <summary>Tables and columns of the initial migration's target model.</summary>
    private static Dictionary<string, HashSet<string>> ExpectedSchema(AppDbContext db, Migration initial)
    {
        var model = db.GetService<IModelRuntimeInitializer>().Initialize(initial.TargetModel!, designTime: true);
        return model.GetRelationalModel().Tables.ToDictionary(t => t.Name, t => t.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>Tables and columns that exist in the database (current schema).</summary>
    private static async Task<Dictionary<string, HashSet<string>>> ReadSchemaAsync(AppDbContext db, CancellationToken ct)
    {
        var sql = db.Database.IsSqlite()
            ? "SELECT m.name, p.name FROM sqlite_master AS m JOIN pragma_table_info(m.name) AS p WHERE m.type = 'table'"
            : db.Database.IsNpgsql()
                ? "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = current_schema()"
                : throw new NotSupportedException($"Baselining is not implemented for {db.Database.ProviderName}.");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                if (!result.TryGetValue(table, out var columns)) result[table] = columns = new HashSet<string>(StringComparer.Ordinal);
                columns.Add(reader.GetString(1));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        return result;
    }
}
