using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        await db.EnableSqliteWalAsync();

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { Roles.SuperAdmin, Roles.CustomerAdmin, Roles.CustomerViewer })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        if (!await db.Plans.AnyAsync())
        {
            db.Plans.AddRange(
                new Plan { Name = "Basic",      MinIntervalSeconds = 300, MaxMonitors = 10,  RetentionDays = 30,  MonthlyPrice = 0m, AllowsSelfSignup = true },
                new Plan { Name = "Standard",   MinIntervalSeconds = 60,  MaxMonitors = 50,  RetentionDays = 90,  MonthlyPrice = 15m, AllowsSelfSignup = true },
                new Plan { Name = "Pro",        MinIntervalSeconds = 30,  MaxMonitors = 200, RetentionDays = 180, MonthlyPrice = 49m, AllowsSelfSignup = true },
                new Plan { Name = "Enterprise", MinIntervalSeconds = 15,  MaxMonitors = 1000, RetentionDays = 365, MonthlyPrice = 199m, AllowsSmsVoice = true });
            foreach (var p in db.Plans.Local)
            {
                ApplyDefaultAiQuota(p);
                ApplyDefaultStatusPageQuota(p);
            }
            await db.SaveChangesAsync();
        }

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var env = sp.GetRequiredService<IHostEnvironment>();
        await SeedSuperAdminAsync(userManager, config, env, logger);

        // Demo data (with its well-known password) is opt-in and only ever created in Development.
        var demoData = config.GetValue("Seed:DemoData", false);
        if (demoData && !env.IsDevelopment())
            logger.LogWarning("Seed:DemoData is ignored outside the Development environment ({Environment}).", env.EnvironmentName);
        else if (demoData && !await db.Customers.AnyAsync())
        {
            var standard = await db.Plans.FirstAsync(p => p.Name == "Standard");
            var demo = new Customer { Name = "Demo Company", ContactEmail = "admin@demo.local", PlanId = standard.Id, AiEnabled = true };
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

    /// <summary>
    /// AI features and monthly budget of the default plans (AI-10, docs/BACKLOG.md): Basic only AI-2 (limited);
    /// Standard + AI-3, AI-5; Pro + AI-4, AI-6; Enterprise + AI-7, AI-8. SuperAdmins edit these on the Plans page.
    /// </summary>
    public static void ApplyDefaultAiQuota(Plan plan)
    {
        string[] basic = [AiFeatures.IncidentInterpretation];
        string[] standard = [.. basic, AiFeatures.SlowdownExplanation, AiFeatures.SettingsSuggestions];
        string[] pro = [.. standard, AiFeatures.CorrelationGrouping, AiFeatures.NarrativeReport];
        string[] enterprise = [.. pro, AiFeatures.QuestionAnswer, AiFeatures.PostmortemDraft];
        (string[] features, int calls, long tokens) = plan.Name switch
        {
            "Basic" => (basic, 50, 500_000L),
            "Standard" => (standard, 500, 5_000_000L),
            "Pro" => (pro, 2_000, 20_000_000L),
            "Enterprise" => (enterprise, 10_000, 100_000_000L),
            _ => ([], 0, 0L)
        };
        plan.AiFeatures = AiFeatures.Format(features);
        plan.AiMonthlyCallLimit = calls;
        plan.AiMonthlyTokenLimit = tokens;
    }

    /// <summary>
    /// Status pages of the default plans (FEAT-4): Basic 1, Standard 3, Pro 10, Enterprise unlimited; custom domains on
    /// Pro and Enterprise. SuperAdmins edit these on the Plans page.
    /// </summary>
    public static void ApplyDefaultStatusPageQuota(Plan plan)
    {
        (plan.MaxStatusPages, plan.AllowsStatusPageCustomDomain) = plan.Name switch
        {
            "Basic" => (1, false),
            "Standard" => (3, false),
            "Pro" => ((int?)10, true),
            "Enterprise" => (null, true),
            _ => (1, false)
        };
    }

    /// <summary>
    /// Creates the SuperAdmin from configuration (Seed:SuperAdminEmail / Seed:SuperAdminPassword, e.g. the
    /// Seed__SuperAdminPassword environment variable). There is no built-in password: outside Development a missing
    /// password while no SuperAdmin exists stops the startup. The seeded account must change its password on first login
    /// unless Seed:SuperAdminMustChangePassword is false. A SuperAdmin user left without its role by a half-failed
    /// seed gets the role back.
    /// </summary>
    private static async Task SeedSuperAdminAsync(UserManager<ApplicationUser> userManager, IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        var strict = !env.IsDevelopment(); // Production, Staging and custom environments fail fast
        var saEmail = config["Seed:SuperAdminEmail"] is { Length: > 0 } e ? e : "superadmin@triggercheck.local";
        var saPassword = config["Seed:SuperAdminPassword"];

        var existing = await userManager.FindByEmailAsync(saEmail);
        if (existing is not null && !await userManager.IsInRoleAsync(existing, Roles.SuperAdmin))
        {
            if (existing.CustomerId is not null)
            {
                // Never promote a customer user that happens to use the configured address.
                var conflict = $"Seed:SuperAdminEmail ({saEmail}) belongs to a customer user; choose another address.";
                if (strict) throw new InvalidOperationException(conflict);
                logger.LogError("{Message}", conflict);
                return;
            }
            var fix = await userManager.AddToRoleAsync(existing, Roles.SuperAdmin);
            if (!fix.Succeeded)
                Fail(strict, logger, "SuperAdmin role repair failed: " + string.Join("; ", fix.Errors.Select(x => x.Description)));
            else
                logger.LogWarning("The SuperAdmin user {Email} had no SuperAdmin role (incomplete seed); the role was added.", saEmail);
        }

        if (existing is not null || (await userManager.GetUsersInRoleAsync(Roles.SuperAdmin)).Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(saPassword))
                logger.Log(strict ? LogLevel.Warning : LogLevel.Information,
                    "Seed:SuperAdminPassword is set but a SuperAdmin already exists; the setting is ignored. Remove it from the configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(saPassword))
        {
            Fail(strict, logger, "No SuperAdmin user exists and no password is configured. " +
                                 "Set Seed:SuperAdminPassword (environment variable Seed__SuperAdminPassword) to create one.");
            return;
        }

        var sa = new ApplicationUser
        {
            UserName = saEmail, Email = saEmail, EmailConfirmed = true, FullName = "Super Admin",
            // Anything but an explicit "false" (including an empty value) keeps the forced change.
            MustChangePassword = !bool.TryParse(config["Seed:SuperAdminMustChangePassword"], out var mustChange) || mustChange
        };
        var r = await userManager.CreateAsync(sa, saPassword);
        if (r.Succeeded) r = await userManager.AddToRoleAsync(sa, Roles.SuperAdmin);
        if (!r.Succeeded) Fail(strict, logger, "SuperAdmin seed failed: " + string.Join("; ", r.Errors.Select(x => x.Description)));
    }

    /// <summary>Throws outside Development (startup stops), logs an error in Development.</summary>
    private static void Fail(bool strict, ILogger logger, string message)
    {
        if (strict) throw new InvalidOperationException(message);
        logger.LogError("{Message}", message);
    }
}
