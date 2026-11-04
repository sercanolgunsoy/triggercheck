using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.SuperAdmin;

/// <summary>Each test uses its own factory (own SQLite file) because the tests mutate the demo customer.</summary>
public class CustomerManagementTests
{
    private static Dictionary<string, string> NewCustomerForm(int planId, string adminEmail, string password = "Secret123", string name = "Acme A.Ş.") => new()
    {
        ["Input.Name"] = name,
        ["Input.ContactEmail"] = "it@acme.test",
        ["Input.PlanId"] = planId.ToString(),
        ["Input.AdminFullName"] = "Ayşe Yılmaz",
        ["Input.AdminEmail"] = adminEmail,
        ["Input.AdminPassword"] = password
    };

    [Fact]
    public async Task Create_customer_with_admin_user_then_user_can_log_in_and_open_app()
    {
        using var f = new TestWebAppFactory();
        var sa = await f.LoginSuperAdminAsync();
        var proId = await f.PlanIdAsync("Pro");

        var resp = await sa.PostFormAsync("/SuperAdmin/Customers/Create", NewCustomerForm(proId, "ayse@acme.test"));
        var details = await sa.FollowAsync(resp);
        Assert.Contains("Acme A.Ş.", details);
        Assert.Contains("ayse@acme.test", details);

        var (customer, user, roles) = await f.WithDbAsync(async db =>
        {
            var c = await db.Customers.SingleAsync(x => x.Name == "Acme A.Ş.");
            var u = await db.Users.SingleAsync(x => x.Email == "ayse@acme.test");
            var r = await (from ur in db.UserRoles join ro in db.Roles on ur.RoleId equals ro.Id where ur.UserId == u.Id select ro.Name).ToListAsync();
            return (c, u, r);
        });
        Assert.Equal(proId, customer.PlanId);
        Assert.True(customer.IsActive);
        Assert.Equal(customer.Id, user.CustomerId);
        Assert.Equal([Roles.CustomerAdmin], roles);

        // The SuperAdmin chose the password, so the new admin must replace it before using the panel.
        Assert.True(user.MustChangePassword);
        var client = await f.LoginAsync("ayse@acme.test", "Secret123");
        var toApp = await client.GetAsync("/App");
        Assert.Equal(HttpStatusCode.Redirect, toApp.StatusCode);
        Assert.Contains("/Account/ChangePassword", toApp.Headers.Location!.ToString());
        var changed = await client.PostFormAsync("/Account/ChangePassword", new()
        {
            ["Input.CurrentPassword"] = "Secret123", ["Input.NewPassword"] = "Ownsecret123", ["Input.ConfirmPassword"] = "Ownsecret123"
        });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/App")).StatusCode);
    }

    [Theory]
    [InlineData(TestWebAppFactory.DemoAdminEmail, "Secret123")] // duplicate email (pre-check)
    [InlineData("new@acme.test", "weakpassword")]                // Identity password rules -> rollback path
    public async Task Failed_user_creation_shows_error_and_leaves_no_orphan_customer(string email, string password)
    {
        using var f = new TestWebAppFactory();
        var sa = await f.LoginSuperAdminAsync();
        var basicId = await f.PlanIdAsync("Basic");
        var before = await f.WithDbAsync(db => db.Customers.CountAsync());

        var resp = await sa.PostFormAsync("/SuperAdmin/Customers/Create", NewCustomerForm(basicId, email, password, name: "Orphan Ltd"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.BodyAsync();
        Assert.Contains("text-danger", html);
        Assert.True(html.Contains("A user with this e-mail address already exists.") || html.Contains("Password", StringComparison.OrdinalIgnoreCase), "error message expected");

        Assert.Equal(before, await f.WithDbAsync(db => db.Customers.CountAsync()));
        Assert.False(await f.WithDbAsync(db => db.Customers.AnyAsync(c => c.Name == "Orphan Ltd")));
        Assert.False(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == "new@acme.test")));
    }

    [Fact]
    public async Task Inactive_plan_is_not_offered_on_create_but_kept_for_current_customer_on_edit()
    {
        using var f = new TestWebAppFactory();
        await f.WithDbAsync(async db =>
        {
            var std = await db.Plans.SingleAsync(p => p.Name == "Standard");
            std.IsActive = false;
            await db.SaveChangesAsync();
        });
        var sa = await f.LoginSuperAdminAsync();
        var demo = await f.DemoCustomerAsync();

        var create = await sa.GetStringAsync("/SuperAdmin/Customers/Create");
        Assert.DoesNotContain(">Standard:", create);
        Assert.Contains(">Basic:", create);

        var edit = await sa.GetStringAsync($"/SuperAdmin/Customers/Edit/{demo.Id}");
        Assert.Contains(">Standard (inactive):", edit);

        // Posting an inactive plan on create is rejected server-side.
        var resp = await sa.PostFormAsync("/SuperAdmin/Customers/Create", NewCustomerForm(demo.PlanId, "x@acme.test"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == "x@acme.test")));
    }

    [Fact]
    public async Task Plan_downgrade_clamps_monitor_intervals()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        Assert.Equal("Standard", demo.Plan.Name);
        var basicId = await f.PlanIdAsync("Basic");
        var sa = await f.LoginSuperAdminAsync();

        var url = $"/SuperAdmin/Customers/Edit/{demo.Id}";
        var resp = await sa.PostFormAsync(url, new()
        {
            ["Input.Name"] = demo.Name, ["Input.ContactEmail"] = demo.ContactEmail, ["Input.PlanId"] = basicId.ToString()
        });
        var html = await sa.FollowAsync(resp);
        Assert.Contains("Check interval raised to the plan minimum (5 min) for 1 monitor(s).", html);

        var intervals = await f.WithDbAsync(db => db.Monitors.Where(m => m.CustomerId == demo.Id).Select(m => m.IntervalSeconds).ToListAsync());
        Assert.All(intervals, i => Assert.Equal(300, i));
        Assert.Equal(basicId, (await f.DemoCustomerAsync()).PlanId);
    }

    [Fact]
    public async Task Plan_downgrade_below_monitor_count_is_allowed_with_warning_and_keeps_monitors()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        await f.AddMonitorsAsync(demo.Id, 11); // 12 monitors total, Basic allows 10
        var basicId = await f.PlanIdAsync("Basic");
        var sa = await f.LoginSuperAdminAsync();

        var resp = await sa.PostFormAsync($"/SuperAdmin/Customers/Edit/{demo.Id}", new()
        {
            ["Input.Name"] = demo.Name, ["Input.ContactEmail"] = demo.ContactEmail, ["Input.PlanId"] = basicId.ToString()
        });
        var html = await sa.FollowAsync(resp);
        Assert.Contains("Warning: the customer has 12 monitors, but the Basic plan allows at most 10.", html);
        Assert.Equal(12, await f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == demo.Id)));
        Assert.Equal(basicId, (await f.DemoCustomerAsync()).PlanId);
    }

    [Fact]
    public async Task Plan_upgrade_does_not_change_intervals()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var entId = await f.PlanIdAsync("Enterprise");
        var sa = await f.LoginSuperAdminAsync();
        var resp = await sa.PostFormAsync($"/SuperAdmin/Customers/Edit/{demo.Id}", new()
        {
            ["Input.Name"] = "Demo Yeni", ["Input.ContactEmail"] = "new@demo.local", ["Input.PlanId"] = entId.ToString()
        });
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var c = await f.WithDbAsync(db => db.Customers.Include(x => x.Monitors).SingleAsync(x => x.Id == demo.Id));
        Assert.Equal("Demo Yeni", c.Name);
        Assert.Equal("new@demo.local", c.ContactEmail);
        Assert.Equal(entId, c.PlanId);
        Assert.All(c.Monitors, m => Assert.Equal(60, m.IntervalSeconds));
    }

    [Fact]
    public async Task Suspend_blocks_login_and_kicks_existing_session_activate_restores()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var customerClient = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await customerClient.GetAsync("/App")).StatusCode);
        var stampBefore = await f.WithDbAsync(db => db.Users.Where(u => u.Email == TestWebAppFactory.DemoAdminEmail).Select(u => u.SecurityStamp).SingleAsync());

        var sa = await f.LoginSuperAdminAsync();
        var detailsUrl = $"/SuperAdmin/Customers/Details/{demo.Id}";
        var resp = await sa.PostFormAsync($"{detailsUrl}?handler=Suspend", [], detailsUrl);
        var html = await sa.FollowAsync(resp);
        Assert.Contains("\"Demo Company\" was suspended.", html);

        Assert.False((await f.DemoCustomerAsync()).IsActive);
        var stampAfter = await f.WithDbAsync(db => db.Users.Where(u => u.Email == TestWebAppFactory.DemoAdminEmail).Select(u => u.SecurityStamp).SingleAsync());
        Assert.NotEqual(stampBefore, stampAfter);

        // Existing session is ended on its very next request, with the "inactive" hint for the login page.
        var kicked = await customerClient.GetAsync("/App");
        Assert.Equal(HttpStatusCode.Redirect, kicked.StatusCode);
        Assert.Contains("/Account/Login", kicked.Headers.Location!.ToString());
        Assert.Contains("reason=inactive", kicked.Headers.Location!.ToString());

        // New login fails; with the correct password the user is told the account is suspended.
        var login = await f.TryLoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("Your account is suspended. Please contact your administrator.", await login.BodyAsync());

        // Activate.
        var before = DateTime.UtcNow.AddSeconds(-1);
        resp = await sa.PostFormAsync($"{detailsUrl}?handler=Activate", [], detailsUrl);
        Assert.Contains("\"Demo Company\" was activated again.", await sa.FollowAsync(resp));
        Assert.True((await f.DemoCustomerAsync()).IsActive);
        var next = await f.WithDbAsync(db => db.Monitors.Where(m => m.CustomerId == demo.Id).Select(m => m.NextCheckAtUtc).ToListAsync());
        Assert.All(next, n => Assert.True(n >= before));

        var again = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/App")).StatusCode);
    }

    [Fact]
    public async Task Delete_requires_typed_name_and_cascades_all_customer_data()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        await f.WithDbAsync(async db =>
        {
            var m = await db.Monitors.FirstAsync(x => x.CustomerId == demo.Id);
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = DateTime.UtcNow, IsUp = false, ResponseTimeMs = 5 });
            db.Incidents.Add(new Incident { MonitorId = m.Id, StartedAtUtc = DateTime.UtcNow, Cause = "timeout" });
            db.Notifications.Add(new Notification { MonitorId = m.Id, SentAtUtc = DateTime.UtcNow, Subject = "DOWN", Success = true });
            db.AlertChannels.Add(new AlertChannel { CustomerId = demo.Id, Name = "Mail", Type = AlertChannelType.Email, Target = "ops@demo.local" });
            await db.SaveChangesAsync();
        });
        var sa = await f.LoginSuperAdminAsync();
        var url = $"/SuperAdmin/Customers/Delete/{demo.Id}";

        var wrong = await sa.PostFormAsync(url, new() { ["ConfirmName"] = "Demo" });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("To confirm, type the customer name exactly as shown.", await wrong.BodyAsync());
        Assert.True(await f.WithDbAsync(db => db.Customers.AnyAsync(c => c.Id == demo.Id)));

        var ok = await sa.PostFormAsync(url, new() { ["ConfirmName"] = demo.Name });
        Assert.Contains("Customer \"Demo Company\" and all its data", await sa.FollowAsync(ok));

        await f.WithDbAsync(async db =>
        {
            Assert.False(await db.Customers.AnyAsync(c => c.Id == demo.Id));
            Assert.Equal(0, await db.Monitors.CountAsync());
            Assert.Equal(0, await db.CheckResults.CountAsync());
            Assert.Equal(0, await db.Incidents.CountAsync());
            Assert.Equal(0, await db.Notifications.CountAsync());
            Assert.Equal(0, await db.AlertChannels.CountAsync());
            Assert.False(await db.Users.AnyAsync(u => u.Email == TestWebAppFactory.DemoAdminEmail));
            // Only the SuperAdmin's role assignment remains.
            Assert.Equal(1, await db.UserRoles.CountAsync());
        });
        var login = await f.TryLoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Customer_user_management_add_reset_deactivate_delete()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var sa = await f.LoginSuperAdminAsync();

        // Add viewer
        var resp = await sa.PostFormAsync($"/SuperAdmin/Customers/AddUser/{demo.Id}", new()
        {
            ["Input.Email"] = "viewer@demo.local", ["Input.FullName"] = "Vera Viewer",
            ["Input.Password"] = "Viewer123", ["Input.Role"] = Roles.CustomerViewer
        });
        Assert.Contains("viewer@demo.local", await sa.FollowAsync(resp));
        var userId = await f.WithDbAsync(db => db.Users.Where(u => u.Email == "viewer@demo.local").Select(u => u.Id).SingleAsync());
        var role = await f.WithDbAsync(db => (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId select r.Name).SingleAsync());
        Assert.Equal(Roles.CustomerViewer, role);
        Task<bool> MustChangeAsync() => f.WithDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.MustChangePassword).SingleAsync());
        Assert.True(await MustChangeAsync()); // password chosen by the SuperAdmin
        var viewer = await f.LoginAsync("viewer@demo.local", "Viewer123");
        Assert.Contains("/Account/ChangePassword", (await viewer.GetAsync("/App")).Headers.Location!.ToString());
        await f.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == userId)).MustChangePassword = false; // as if the user had changed it
            await db.SaveChangesAsync();
        });

        // Invalid role is rejected
        resp = await sa.PostFormAsync($"/SuperAdmin/Customers/AddUser/{demo.Id}", new()
        {
            ["Input.Email"] = "evil@demo.local", ["Input.FullName"] = "Evil",
            ["Input.Password"] = "Viewer123", ["Input.Role"] = Roles.SuperAdmin
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == "evil@demo.local")));

        // Reset password
        var resetUrl = $"/SuperAdmin/Customers/ResetPassword/{demo.Id}/{userId}";
        resp = await sa.PostFormAsync(resetUrl, new() { ["NewPassword"] = "Changed123" });
        Assert.Contains("The password of viewer@demo.local was reset", await sa.FollowAsync(resp));
        Assert.Equal(HttpStatusCode.OK, (await f.TryLoginAsync("viewer@demo.local", "Viewer123")).StatusCode); // old pw fails
        Assert.True(await MustChangeAsync()); // a reset password must be replaced again
        viewer = await f.LoginAsync("viewer@demo.local", "Changed123");

        // Deactivate -> existing session kicked, login blocked
        var detailsUrl = $"/SuperAdmin/Customers/Details/{demo.Id}";
        resp = await sa.PostFormAsync($"{detailsUrl}?handler=ToggleUser", new() { ["userId"] = userId }, detailsUrl);
        Assert.Contains("viewer@demo.local was deactivated", await sa.FollowAsync(resp));
        var kicked = await viewer.GetAsync("/App");
        Assert.Equal(HttpStatusCode.Redirect, kicked.StatusCode);
        Assert.Contains("/Account/Login", kicked.Headers.Location!.ToString());
        Assert.Contains("reason=inactive", kicked.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await f.TryLoginAsync("viewer@demo.local", "Changed123")).StatusCode);

        // Reactivate -> can log in
        resp = await sa.PostFormAsync($"{detailsUrl}?handler=ToggleUser", new() { ["userId"] = userId }, detailsUrl);
        Assert.Contains("viewer@demo.local was activated.", await sa.FollowAsync(resp));
        await f.LoginAsync("viewer@demo.local", "Changed123");

        // Delete
        resp = await sa.PostFormAsync($"{detailsUrl}?handler=DeleteUser", new() { ["userId"] = userId }, detailsUrl);
        Assert.Contains("viewer@demo.local was deleted.", await sa.FollowAsync(resp));
        Assert.False(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == userId)));
    }

    [Fact]
    public async Task User_actions_are_scoped_to_the_customer_in_the_url()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var sa = await f.LoginSuperAdminAsync();
        var otherId = await f.WithDbAsync(async db =>
        {
            var c = new Customer { Name = "Other", ContactEmail = "o@o.test", PlanId = demo.PlanId };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });
        var demoAdminId = await f.WithDbAsync(db => db.Users.Where(u => u.Email == TestWebAppFactory.DemoAdminEmail).Select(u => u.Id).SingleAsync());

        var otherDetails = $"/SuperAdmin/Customers/Details/{otherId}";
        var resp = await sa.PostFormAsync($"{otherDetails}?handler=DeleteUser", new() { ["userId"] = demoAdminId }, otherDetails);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.True(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == demoAdminId)));
        Assert.Equal(HttpStatusCode.NotFound, (await sa.GetAsync($"/SuperAdmin/Customers/ResetPassword/{otherId}/{demoAdminId}")).StatusCode);
    }

    /// <summary>Throws for "boom@" e-mails, simulating an unexpected failure inside customer creation.</summary>
    private sealed class ThrowingUserValidator : IUserValidator<ApplicationUser>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
            user.Email?.StartsWith("boom@") == true
                ? throw new InvalidOperationException("SECRET-INTERNAL-DETAIL")
                : Task.FromResult(IdentityResult.Success);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Message, Exception? Exception)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider p) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) p.Entries.Enqueue((formatter(state, exception), exception));
            }
        }
    }

    [Fact]
    public async Task Unexpected_creation_error_shows_generic_message_with_logged_reference_not_exception_text()
    {
        using var f = new TestWebAppFactory();
        var logs = new CapturingLoggerProvider();
        using var host = f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddScoped<IUserValidator<ApplicationUser>, ThrowingUserValidator>();
            s.AddSingleton<ILoggerProvider>(logs);
        }));
        var sa = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var login = await sa.PostFormAsync("/Account/Login", new() { ["Input.Email"] = TestWebAppFactory.SuperAdminEmail, ["Input.Password"] = TestWebAppFactory.SuperAdminPassword });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriggerCheck.Infrastructure.Data.AppDbContext>();
        var basicId = await db.Plans.Where(p => p.Name == "Basic").Select(p => p.Id).SingleAsync();

        var resp = await sa.PostFormAsync("/SuperAdmin/Customers/Create", NewCustomerForm(basicId, "boom@acme.test", name: "Boom Ltd"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.BodyAsync();
        Assert.DoesNotContain("SECRET-INTERNAL-DETAIL", html);
        var m = Regex.Match(html, @"The customer could not be created because of an unexpected error\. Reference: ([^<\s]+)");
        Assert.True(m.Success, "generic error with a reference expected");

        // The same reference is logged together with the exception.
        var entry = Assert.Single(logs.Entries, e => e.Message.Contains("Customer creation failed"));
        Assert.Contains(m.Groups[1].Value, entry.Message);
        Assert.Equal("SECRET-INTERNAL-DETAIL", entry.Exception?.Message);
        Assert.False(await db.Customers.AnyAsync(c => c.Name == "Boom Ltd"));
    }

    [Fact]
    public async Task Customer_list_search_and_filters()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        await f.WithDbAsync(async db =>
        {
            db.Customers.Add(new Customer { Name = "Zeta Bilişim", ContactEmail = "info@zeta.test", PlanId = demo.PlanId, IsActive = false });
            await db.SaveChangesAsync();
        });
        var sa = await f.LoginSuperAdminAsync();

        var all = await sa.GetStringAsync("/SuperAdmin/Customers/Index");
        Assert.Contains("Zeta Bilişim", System.Net.WebUtility.HtmlDecode(all));
        Assert.Contains("Demo Company", System.Net.WebUtility.HtmlDecode(all));

        var suspended = System.Net.WebUtility.HtmlDecode(await sa.GetStringAsync("/SuperAdmin/Customers/Index?State=suspended"));
        Assert.Contains("Zeta Bilişim", suspended);
        Assert.DoesNotContain("Demo Company", suspended);

        // Search matches a user's e-mail too.
        var byUser = System.Net.WebUtility.HtmlDecode(await sa.GetStringAsync("/SuperAdmin/Customers/Index?Q=ADMIN@demo"));
        Assert.Contains("Demo Company", byUser);
        Assert.DoesNotContain("Zeta Bilişim", byUser);

        var basicId = await f.PlanIdAsync("Basic");
        var byPlan = System.Net.WebUtility.HtmlDecode(await sa.GetStringAsync($"/SuperAdmin/Customers/Index?PlanId={basicId}"));
        Assert.Contains("No records found.", byPlan);
    }
}
