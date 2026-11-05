using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>
/// OPS-1 / OPS-2: EF Core migrations, the EnsureCreated baseline, the DB defaults of the initial migration and the provider
/// switch. The database tests run on the provider of the test run (<see cref="TestDatabase"/>).
/// </summary>
public class MigrationTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    [Theory]
    [InlineData(DatabaseProvider.Sqlite, "Data Source=unused.db")]
    [InlineData(DatabaseProvider.Postgres, "Host=unused.invalid;Database=unused")]
    public void Model_has_no_changes_without_a_migration(DatabaseProvider provider, string connectionString)
    {
        // Fails when the model changes without `dotnet ef migrations add` for each provider (see docs/DEPLOYMENT.md
        // "Database and migrations"). Needs no database: both providers are checked in every test run.
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseOptions.Configure(options, provider, connectionString);
        using var db = new AppDbContext(options.Options);
        Assert.False(db.Database.HasPendingModelChanges(), $"The model has changes that are not in a {provider} migration: add one.");
    }

    [Theory]
    [InlineData(null, DatabaseProvider.Sqlite)]
    [InlineData("sqlite", DatabaseProvider.Sqlite)]
    [InlineData("Postgres", DatabaseProvider.Postgres)]
    [InlineData("postgresql", DatabaseProvider.Postgres)]
    public void Provider_is_read_from_configuration(string? value, DatabaseProvider expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = value }).Build();
        Assert.Equal(expected, DatabaseOptions.GetProvider(config));
    }

    [Fact]
    public void Unknown_provider_and_postgres_without_connection_string_are_refused()
    {
        var unknown = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = "SqlServer" }).Build();
        Assert.Throws<InvalidOperationException>(() => DatabaseOptions.GetProvider(unknown));
        var empty = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => DatabaseOptions.GetConnectionString(empty, DatabaseProvider.Postgres));
        Assert.Equal(DatabaseOptions.DefaultSqliteConnectionString, DatabaseOptions.GetConnectionString(empty, DatabaseProvider.Sqlite));
    }

    [Fact]
    public async Task Migrate_creates_the_schema_on_an_empty_database()
    {
        using var tdb = TestDatabase.Create("mig", withSchema: false);
        await using (var db = tdb.NewContext())
        {
            await DatabaseMigrator.MigrateAsync(db, Log);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        }

        await using (var db = tdb.NewContext())
        {
            // DB defaults required by docs/BACKLOG.md OPS-1 (for rows written outside the app).
            var dbTrue = TestDatabase.UsePostgres ? "true" : "1";
            Assert.Equal(dbTrue, await ColumnDefaultAsync(db, "Customers", "AlertContactEmailWhenNoChannel"));
            Assert.Equal(dbTrue, await ColumnDefaultAsync(db, "Customers", "AlertGroupingEnabled"));
            Assert.Equal("1", await ColumnDefaultAsync(db, "Plans", "MaxRegions"));
            Assert.Equal("0", await ColumnDefaultAsync(db, "AiDecisions", "ModelCalls"));
            Assert.Null(await ColumnDefaultAsync(db, "Plans", "MaxStatusPages"));

            // A second run is a no-op.
            Assert.False(await DatabaseMigrator.BaselineAsync(db, Log));
            await DatabaseMigrator.MigrateAsync(db, Log);
        }
    }

    [Fact]
    public async Task Values_equal_to_the_clr_default_are_written_despite_the_db_default()
    {
        using var tdb = TestDatabase.Create("mig", withSchema: false);
        int planId, customerId;
        long decisionId;
        await using (var db = tdb.NewContext())
        {
            await DatabaseMigrator.MigrateAsync(db, Log);
            var plan = new Plan { Name = "All regions", MinIntervalSeconds = 60, MaxMonitors = 1, MaxRegions = 0, MaxStatusPages = null };
            var customer = new Customer { Name = "C", Plan = plan, AlertContactEmailWhenNoChannel = false, AlertGroupingEnabled = false };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            var decision = new AiDecision { CustomerId = null, Feature = "platform", ModelCalls = 0 }; // AI-12: platform decision
            db.AiDecisions.Add(decision);
            await db.SaveChangesAsync();
            (planId, customerId, decisionId) = (plan.Id, customer.Id, decision.Id);
        }

        await using (var db = tdb.NewContext())
        {
            var plan = await db.Plans.SingleAsync(p => p.Id == planId);
            Assert.Equal(0, plan.MaxRegions);
            Assert.Null(plan.MaxStatusPages);
            var customer = await db.Customers.SingleAsync(c => c.Id == customerId);
            Assert.False(customer.AlertContactEmailWhenNoChannel);
            Assert.False(customer.AlertGroupingEnabled);
            var decision = await db.AiDecisions.SingleAsync(d => d.Id == decisionId);
            Assert.Null(decision.CustomerId);
            Assert.Equal(0, decision.ModelCalls);
        }
    }

    [Fact]
    public async Task EnsureCreated_database_is_baselined_and_keeps_its_data()
    {
        using var tdb = TestDatabase.Create("mig", withSchema: false);
        await using (var db = tdb.NewContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Plans.Add(new Plan { Name = "Kept", MinIntervalSeconds = 60, MaxMonitors = 1 });
            await db.SaveChangesAsync();
        }

        await using (var db = tdb.NewContext())
        {
            Assert.True(await DatabaseMigrator.BaselineAsync(db, Log));
            await DatabaseMigrator.MigrateAsync(db, Log);
            Assert.Equal(db.Database.GetMigrations().First(), (await db.Database.GetAppliedMigrationsAsync()).First());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.True(await db.Plans.AnyAsync(p => p.Name == "Kept"));
            await DatabaseMigrator.EnsureUpToDateAsync(db);
        }
    }

    [Fact]
    public async Task Older_EnsureCreated_schema_is_refused_without_changes()
    {
        using var tdb = TestDatabase.Create("mig", withSchema: false);
        await using (var db = tdb.NewContext())
        {
            await db.Database.EnsureCreatedAsync();
            // As created by a build before AI-4 / AI-8.
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Customers" DROP COLUMN "AlertGroupingEnabled" """);
            await db.Database.ExecuteSqlRawAsync("""DROP TABLE "PostmortemDrafts" """);
        }

        await using (var db = tdb.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrator.MigrateAsync(db, Log));
            Assert.Contains("Customers.AlertGroupingEnabled", ex.Message);
            Assert.Contains("PostmortemDrafts (table)", ex.Message);
            Assert.Contains("DEPLOYMENT.md", ex.Message);
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        }
    }

    [Fact]
    public async Task Startup_check_without_migrate_refuses_an_outdated_database()
    {
        using var tdb = TestDatabase.Create("mig", withSchema: false);
        await using var db = tdb.NewContext();
        await db.Database.EnsureCreatedAsync(); // no history: the migrate step has not run
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrator.EnsureUpToDateAsync(db));
        Assert.Contains("migrate", ex.Message);
    }

    private static async Task<string?> ColumnDefaultAsync(AppDbContext db, string table, string column) =>
        await db.Database.SqlQueryRaw<string?>(TestDatabase.UsePostgres
                ? "SELECT column_default AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0} AND column_name = {1}"
                : "SELECT dflt_value AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1}", table, column)
            .SingleAsync();
}
