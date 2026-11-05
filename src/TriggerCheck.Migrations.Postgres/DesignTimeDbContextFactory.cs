using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Migrations.Postgres;

/// <summary>Used by <c>dotnet ef</c> only; adding a migration never opens the connection.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseOptions.Configure(options, DatabaseProvider.Postgres, "Host=localhost;Database=triggercheck_design_time");
        return new AppDbContext(options.Options);
    }
}
