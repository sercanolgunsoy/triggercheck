using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.SuperAdmin;

public class PlanManagementTests
{
    private static Dictionary<string, string> PlanForm(string name, int interval, int maxMonitors = 5, int retention = 30, string price = "9.90", bool active = true) => new()
    {
        ["Input.Name"] = name,
        ["Input.MinIntervalSeconds"] = interval.ToString(),
        ["Input.MaxMonitors"] = maxMonitors.ToString(),
        ["Input.RetentionDays"] = retention.ToString(),
        ["Input.MonthlyPrice"] = price,
        ["Input.IsActive"] = active ? "true" : "false"
    };

    [Fact]
    public async Task Create_plan_validates_and_persists()
    {
        using var f = new TestWebAppFactory();
        var sa = await f.LoginSuperAdminAsync();
        const string url = "/SuperAdmin/Plans/Create";

        // Unsupported interval
        var r = await sa.PostFormAsync(url, PlanForm("Starter", 45));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Unsupported interval. Valid values: 5 min, 1 min, 30 sec, 15 sec.", await r.BodyAsync());

        // Duplicate name (case-insensitive)
        r = await sa.PostFormAsync(url, PlanForm("standard", 60));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("A plan with this name already exists.", await r.BodyAsync());

        // Range checks
        r = await sa.PostFormAsync(url, PlanForm("Starter", 60, maxMonitors: 0, retention: 4000, price: "-1"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.BodyAsync();
        Assert.Contains("Max monitors must be at least 1.", body);
        Assert.Contains("Retention must be between 1 and 3650 days.", body);
        Assert.Contains("Monthly price must be 0 or greater.", body);

        Assert.False(await f.WithDbAsync(db => db.Plans.AnyAsync(p => p.Name == "Starter")));

        // Valid
        r = await sa.PostFormAsync(url, PlanForm("Starter", 60, maxMonitors: 5, retention: 14, price: "9.90", active: false));
        Assert.Contains("Starter", await sa.FollowAsync(r));
        var plan = await f.WithDbAsync(db => db.Plans.SingleAsync(p => p.Name == "Starter"));
        Assert.Equal(60, plan.MinIntervalSeconds);
        Assert.Equal(5, plan.MaxMonitors);
        Assert.Equal(14, plan.RetentionDays);
        Assert.Equal(9.90m, plan.MonthlyPrice);
        Assert.False(plan.IsActive);
    }

    [Theory]
    [InlineData("9.90", "9.90")]
    [InlineData("9,90", "9.90")]
    [InlineData("1.234,50", "1234.50")]
    [InlineData("1,234.50", "1234.50")]
    [InlineData("15", "15")]
    public void Price_parsing_is_culture_independent(string raw, string expected)
    {
        Assert.True(TriggerCheck.Web.Areas.SuperAdmin.Services.FlexibleDecimalModelBinder.TryParse(raw, out var v));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), v);
    }

    [Fact]
    public async Task Edit_plan_rejects_unsupported_interval()
    {
        using var f = new TestWebAppFactory();
        var sa = await f.LoginSuperAdminAsync();
        var stdId = await f.PlanIdAsync("Standard");
        var r = await sa.PostFormAsync($"/SuperAdmin/Plans/Edit/{stdId}", PlanForm("Standard", 45, 50, 90, "15"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Unsupported interval. Valid values: 5 min, 1 min, 30 sec, 15 sec.", await r.BodyAsync());
        Assert.Equal(60, await f.WithDbAsync(db => db.Plans.Where(p => p.Id == stdId).Select(p => p.MinIntervalSeconds).SingleAsync()));

        // Renaming to another existing plan's name is rejected; keeping its own name is fine.
        r = await sa.PostFormAsync($"/SuperAdmin/Plans/Edit/{stdId}", PlanForm("Pro", 60, 50, 90, "15"));
        Assert.Contains("A plan with this name already exists.", await r.BodyAsync());
    }

    [Fact]
    public async Task Raising_plan_min_interval_clamps_monitors_of_all_customers_on_plan()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var stdId = demo.PlanId;
        // A second customer on Standard with a 60s and a 300s monitor, and a Pro customer whose 30s monitor must stay.
        var (otherId, proCustomerId) = await f.WithDbAsync(async db =>
        {
            var proId = await db.Plans.Where(p => p.Name == "Pro").Select(p => p.Id).SingleAsync();
            var other = new Customer { Name = "Other", ContactEmail = "o@o.test", PlanId = stdId };
            var pro = new Customer { Name = "ProCo", ContactEmail = "p@p.test", PlanId = proId };
            db.Customers.AddRange(other, pro);
            await db.SaveChangesAsync();
            return (other.Id, pro.Id);
        });
        await f.AddMonitorsAsync(otherId, 1, 60, "Fast");
        await f.AddMonitorsAsync(otherId, 1, 300, "Slow");
        await f.AddMonitorsAsync(proCustomerId, 1, 30, "Pro");

        var sa = await f.LoginSuperAdminAsync();
        var r = await sa.PostFormAsync($"/SuperAdmin/Plans/Edit/{stdId}", PlanForm("Standard", 300, 50, 90, "15"));
        var html = await sa.FollowAsync(r);
        Assert.Contains("Check interval raised to the new plan minimum (5 min) for 2 monitor(s).", html);

        var intervals = await f.WithDbAsync(db => db.Monitors.Select(m => new { m.CustomerId, m.IntervalSeconds }).ToListAsync());
        Assert.All(intervals.Where(m => m.CustomerId != proCustomerId), m => Assert.Equal(300, m.IntervalSeconds));
        Assert.Equal(30, intervals.Single(m => m.CustomerId == proCustomerId).IntervalSeconds);
    }

    [Fact]
    public async Task Delete_plan_is_blocked_while_in_use_and_allowed_otherwise()
    {
        using var f = new TestWebAppFactory();
        var sa = await f.LoginSuperAdminAsync();
        var stdId = await f.PlanIdAsync("Standard");
        var basicId = await f.PlanIdAsync("Basic");

        var r = await sa.PostFormAsync($"/SuperAdmin/Plans/Index?handler=Delete&id={stdId}", [], "/SuperAdmin/Plans/Index");
        Assert.Contains("cannot be deleted", await sa.FollowAsync(r));
        Assert.True(await f.WithDbAsync(db => db.Plans.AnyAsync(p => p.Id == stdId)));

        r = await sa.PostFormAsync($"/SuperAdmin/Plans/Index?handler=Delete&id={basicId}", [], "/SuperAdmin/Plans/Index");
        Assert.Contains("Plan \"Basic\" was deleted.", await sa.FollowAsync(r));
        Assert.False(await f.WithDbAsync(db => db.Plans.AnyAsync(p => p.Id == basicId)));
    }

    [Fact]
    public async Task Plan_list_shows_customer_counts()
    {
        using var f = new TestWebAppFactory();
        var sa = await f.LoginSuperAdminAsync();
        var html = WebUtility.HtmlDecode(await sa.GetStringAsync("/SuperAdmin/Plans/Index"));
        foreach (var name in new[] { "Basic", "Standard", "Pro", "Enterprise" }) Assert.Contains(name, html);
        var stdId = await f.PlanIdAsync("Standard");
        Assert.Contains($"planId={stdId}\">1</a>", html);
    }
}
