using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default") ?? "Data Source=triggercheck.db";
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(cs));
        // Monitoring engine runs many short-lived, parallel units of work -> factory.
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(cs), ServiceLifetime.Scoped);
        return services;
    }
}
