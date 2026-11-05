using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>OPS-1: EF Core migrations, the EnsureCreated baseline and the DB defaults of the initial migration.</summary>
public class MigrationTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    [Fact]
    public void Model_has_no_changes_without_a_migration()
    {
        // Fails when the model changes without `dotnet ef migrations add` (see docs/DEPLOYMENT.md "Database and migrations").
        using var tdb = TestDatabase.Create("mig");
        using var db = tdb.NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The model has changes that are not in a migration: add one.");
    }

    [Fact]
    public async Task Migrate_creates_the_schema_on_an_empty_database()
    {
        using var tdb = TestDatabase.Create("mig");
        await using (var db = tdb.NewContext())
        {
            await DatabaseMigrator.MigrateAsync(db, Log);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        }

        await using (var db = tdb.NewContext())
        {
            // DB defaults required by docs/BACKLOG.md OPS-1 (for rows written outside the app).
            Assert.Equal("1", await ColumnDefaultAsync(db, "Customers", "AlertContactEmailWhenNoChannel"));
            Assert.Equal("1", await ColumnDefaultAsync(db, "Customers", "AlertGroupingEnabled"));
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
        using var tdb = TestDatabase.Create("mig");
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
        using var tdb = TestDatabase.Create("mig");
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
        using var tdb = TestDatabase.Create("mig");
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
            Assert.False(await db.GetService<IHistoryRepository>().ExistsAsync());
        }
    }

    [Fact]
    public async Task Startup_check_without_migrate_refuses_an_outdated_database()
    {
        using var tdb = TestDatabase.Create("mig");
        await using var db = tdb.NewContext();
        await db.Database.EnsureCreatedAsync(); // no history: the migrate step has not run
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrator.EnsureUpToDateAsync(db));
        Assert.Contains("migrate", ex.Message);
    }

    private static async Task<string?> ColumnDefaultAsync(AppDbContext db, string table, string column) =>
        await db.Database.SqlQueryRaw<string?>(
                "SELECT dflt_value AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1}", table, column)
            .SingleAsync();
}
