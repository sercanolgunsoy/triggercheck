using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Infrastructure.Data;

public static class DbSeeder
{
    /// <summary>Creates the schema (if missing), roles, default plans, the SuperAdmin user and (optionally) a demo customer.</summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var config = sp.GetRequiredService<IConfiguration>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");

        await db.Database.EnsureCreatedAsync();

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { Roles.SuperAdmin, Roles.CustomerAdmin, Roles.CustomerViewer })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        if (!await db.Plans.AnyAsync())
        {
            db.Plans.AddRange(
                new Plan { Name = "Basic",      MinIntervalSeconds = 300, MaxMonitors = 10,  RetentionDays = 30,  MonthlyPrice = 0m },
                new Plan { Name = "Standard",   MinIntervalSeconds = 60,  MaxMonitors = 50,  RetentionDays = 90,  MonthlyPrice = 15m },
                new Plan { Name = "Pro",        MinIntervalSeconds = 30,  MaxMonitors = 200, RetentionDays = 180, MonthlyPrice = 49m },
                new Plan { Name = "Enterprise", MinIntervalSeconds = 15,  MaxMonitors = 1000, RetentionDays = 365, MonthlyPrice = 199m });
            await db.SaveChangesAsync();
        }

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var saEmail = config["Seed:SuperAdminEmail"] ?? "superadmin@triggercheck.local";
        var saPassword = config["Seed:SuperAdminPassword"] ?? "SuperAdmin123!";
        if (await userManager.FindByEmailAsync(saEmail) is null)
        {
            var sa = new ApplicationUser { UserName = saEmail, Email = saEmail, EmailConfirmed = true, FullName = "Super Admin" };
            var r = await userManager.CreateAsync(sa, saPassword);
            if (r.Succeeded) await userManager.AddToRoleAsync(sa, Roles.SuperAdmin);
            else logger.LogError("SuperAdmin seed failed: {Errors}", string.Join("; ", r.Errors.Select(e => e.Description)));
        }

        if (config.GetValue("Seed:DemoData", true) && !await db.Customers.AnyAsync())
        {
            var standard = await db.Plans.FirstAsync(p => p.Name == "Standard");
            var demo = new Customer { Name = "Demo Şirketi", ContactEmail = "admin@demo.local", PlanId = standard.Id };
            db.Customers.Add(demo);
            await db.SaveChangesAsync();

            var admin = new ApplicationUser { UserName = "admin@demo.local", Email = "admin@demo.local", EmailConfirmed = true, FullName = "Demo Admin", CustomerId = demo.Id };
            var r = await userManager.CreateAsync(admin, "Demo123!");
            if (r.Succeeded) await userManager.AddToRoleAsync(admin, Roles.CustomerAdmin);

            db.Monitors.Add(new Domain.Entities.Monitor
            {
                CustomerId = demo.Id, Name = "Example.com", Type = MonitorType.Http,
                Target = "https://example.com", IntervalSeconds = 60, NextCheckAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }
}
