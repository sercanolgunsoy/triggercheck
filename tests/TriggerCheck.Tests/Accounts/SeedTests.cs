using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Accounts;

/// <summary>SEC-2: no built-in credentials; demo data is opt-in and never seeded in Production.</summary>
public class SeedTests
{
    private static WebApplicationFactory<Program> Production(TestWebAppFactory f, string password, string demoData = "true") =>
        f.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Seed:SuperAdminPassword", password);
            b.UseSetting("Seed:DemoData", demoData);
            b.UseSetting("Seed:SuperAdminMustChangePassword", null);
        });

    [Fact]
    public void Base_appsettings_contains_no_seed_password_and_no_demo_data()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "TriggerCheck.sln"))) dir = dir.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "src", "TriggerCheck.Web", "appsettings.json")));
        var seed = json.RootElement.GetProperty("Seed");
        Assert.False(seed.TryGetProperty("SuperAdminPassword", out _));
        Assert.False(seed.GetProperty("DemoData").GetBoolean());
    }

    [Fact]
    public void Production_without_superadmin_password_fails_to_start()
    {
        using var f = new TestWebAppFactory();
        using var host = Production(f, "");
        var ex = Assert.ThrowsAny<Exception>(() => host.Services);
        Assert.Contains("Seed__SuperAdminPassword", ex.ToString());
    }

    [Fact]
    public async Task Production_seeds_superadmin_from_configuration_but_never_demo_data()
    {
        using var f = new TestWebAppFactory();
        using var host = Production(f, "FromSecret123");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(await db.Customers.AnyAsync());
        var sa = await db.Users.SingleAsync();
        Assert.Equal(TestWebAppFactory.SuperAdminEmail, sa.Email);
        Assert.True(sa.MustChangePassword); // default: forced change on first login
        var um = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<TriggerCheck.Infrastructure.Identity.ApplicationUser>>();
        Assert.True(await um.CheckPasswordAsync(sa, "FromSecret123"));
        Assert.True(await um.IsInRoleAsync(sa, Roles.SuperAdmin));
    }
}
