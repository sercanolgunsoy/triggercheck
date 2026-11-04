using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Client;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.SuperAdmin;

namespace TriggerCheck.Tests.Ai;

/// <summary>AI settings page, AI content partial + feedback, SuperAdmin AI usage, plan AI quota editing.</summary>
public class AiWebTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string ViewerEmail = "ai-viewer@demo.local";
    private const string ViewerPassword = "Viewer123!";

    private async Task<HttpClient> ViewerAsync()
    {
        var cid = await f.DemoCustomerIdAsync();
        if (!await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == ViewerEmail)))
            await f.AddUserAsync(cid, ViewerEmail, ViewerPassword, Roles.CustomerViewer);
        return await f.LoginAsync(ViewerEmail, ViewerPassword);
    }

    private Task<long> AddDecisionAsync(int customerId, AiDecisionStatus status = AiDecisionStatus.Succeeded, string? json = FakeAiClient.CannedJson) =>
        f.WithDbAsync(async db =>
        {
            var d = new AiDecision
            {
                CustomerId = customerId, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                Model = "claude-opus-5-5", Effort = "low", Status = status, OutputJson = status == AiDecisionStatus.Succeeded ? json : null,
                InputTokens = 1000, OutputTokens = 200, CreatedAtUtc = DateTime.UtcNow
            };
            db.AiDecisions.Add(d);
            await db.SaveChangesAsync();
            return d.Id;
        });

    [Fact]
    public async Task Default_plans_carry_the_backlog_AI_tiers()
    {
        var plans = await f.WithDbAsync(db => db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Name));
        Assert.Equal([AiFeatures.IncidentInterpretation], AiFeatures.Parse(plans["Basic"].AiFeatures));
        Assert.Equal([AiFeatures.IncidentInterpretation, AiFeatures.SlowdownExplanation, AiFeatures.SettingsSuggestions], AiFeatures.Parse(plans["Standard"].AiFeatures));
        Assert.Contains(AiFeatures.CorrelationGrouping, AiFeatures.Parse(plans["Pro"].AiFeatures));
        Assert.Contains(AiFeatures.NarrativeReport, AiFeatures.Parse(plans["Pro"].AiFeatures));
        Assert.DoesNotContain(AiFeatures.QuestionAnswer, AiFeatures.Parse(plans["Pro"].AiFeatures));
        Assert.Equal(7, AiFeatures.Parse(plans["Enterprise"].AiFeatures).Count);
        Assert.True(plans["Basic"].AiMonthlyCallLimit > 0 && plans["Basic"].AiMonthlyCallLimit < plans["Standard"].AiMonthlyCallLimit);
    }

    [Fact]
    public void Existing_customers_default_to_AI_off() => Assert.False(new Customer().AiEnabled);

    [Fact]
    public async Task Admin_sees_AI_page_with_privacy_note_and_can_toggle_opt_in()
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(s => s.SetProperty(c => c.AiEnabled, false)));
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var html = await admin.GetHtmlAsync("/App/Ai");
        Assert.Contains("id=\"ai-privacy-note\"", html);
        Assert.Contains("Only masked technical data", html);
        Assert.Contains("id=\"ai-service-off\"", html); // Ai:Enabled is false in tests
        Assert.Contains("Enable AI features", html);
        Assert.Contains("href=\"/App/Ai\"", html); // sidebar

        var r = await admin.PostFormAsync("/App/Ai", new() { ["enabled"] = "true" });
        AppTestData.AssertRedirect(r, "/App/Ai");
        Assert.True(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AiEnabled).SingleAsync()));

        r = await admin.PostFormAsync("/App/Ai", new() { ["enabled"] = "false" });
        AppTestData.AssertRedirect(r, "/App/Ai");
        Assert.False(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AiEnabled).SingleAsync()));
    }

    [Fact]
    public async Task Viewer_sees_AI_settings_read_only_and_cannot_toggle()
    {
        var viewer = await ViewerAsync();
        var html = await viewer.GetHtmlAsync("/App/Ai");
        Assert.Contains("Only customer admins can change this.", html);
        Assert.DoesNotContain("id=\"ai-optin-form\"", html);

        var cid = await f.DemoCustomerIdAsync();
        var before = await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AiEnabled).SingleAsync());
        // Antiforgery token from another page of the same session.
        var r = await viewer.PostFormAsync("/App/Ai", new() { ["enabled"] = (!before).ToString() }, formPageUrl: "/App/Account");
        AppTestData.AssertAccessDenied(r);
        Assert.Equal(before, await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AiEnabled).SingleAsync()));
    }

    [Fact]
    public async Task AI_content_partial_renders_badge_confidence_evidence_and_collapsed_low_hypotheses()
    {
        var cid = await f.DemoCustomerIdAsync();
        var id = await AddDecisionAsync(cid);
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var html = await admin.GetHtmlAsync("/App/Ai");
        Assert.Contains($"data-ai-decision=\"{id}\"", html);
        Assert.Contains("AI-generated", html);
        Assert.Contains("The monitor failed because the target returned HTTP 503", html);
        Assert.Contains("High confidence", html);
        Assert.Contains("3 consecutive checks returned status 503", html);
        Assert.Contains("<details class=\"mb-3 ai-possible\">", html);
        Assert.Contains("Possible causes (low confidence): 1", html);
        Assert.Contains("Whether a deployment happened at that time.", html);
        Assert.Contains("Was this helpful?", html);
        Assert.DoesNotContain("onclick", html);
    }

    [Fact]
    public async Task AI_content_is_localized_to_Turkish()
    {
        var cid = await f.DemoCustomerIdAsync();
        await AddDecisionAsync(cid);
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        await admin.SetLanguageAsync("tr");
        var html = await admin.GetHtmlAsync("/App/Ai");
        Assert.Contains("Yapay zekâ tarafından üretildi", html);
        Assert.Contains("Bu faydalı oldu mu?", html);
        Assert.Contains("Gizlilik", html);
    }

    [Fact]
    public async Task Viewer_can_give_feedback_on_own_tenant_only()
    {
        var cid = await f.DemoCustomerIdAsync();
        var mine = await AddDecisionAsync(cid);
        var otherCustomer = await f.AddCustomerAsync("AI Other Tenant");
        var theirs = await AddDecisionAsync(otherCustomer);
        var viewer = await ViewerAsync();
        var viewerId = await f.UserIdAsync(ViewerEmail);

        var r = await viewer.PostFormAsync("/App/Ai/Feedback", new() { ["decisionId"] = mine.ToString(), ["value"] = "-1", ["returnUrl"] = "/App/Ai" }, formPageUrl: "/App/Ai");
        AppTestData.AssertRedirect(r, "/App/Ai");
        var d = await f.WithDbAsync(db => db.AiDecisions.AsNoTracking().SingleAsync(x => x.Id == mine));
        Assert.Equal(-1, d.UserFeedback);
        Assert.Equal(viewerId, d.FeedbackByUserId);
        Assert.NotNull(d.FeedbackAtUtc);

        // Another tenant's decision: 404 and unchanged.
        r = await viewer.PostFormAsync("/App/Ai/Feedback", new() { ["decisionId"] = theirs.ToString(), ["value"] = "1" }, formPageUrl: "/App/Ai");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(await f.WithDbAsync(db => db.AiDecisions.Where(x => x.Id == theirs).Select(x => x.UserFeedback).SingleAsync()));

        // Invalid value and off-site return URLs are refused.
        r = await viewer.PostFormAsync("/App/Ai/Feedback", new() { ["decisionId"] = mine.ToString(), ["value"] = "5" }, formPageUrl: "/App/Ai");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await viewer.PostFormAsync("/App/Ai/Feedback", new() { ["decisionId"] = mine.ToString(), ["value"] = "1", ["returnUrl"] = "https://evil.example/" }, formPageUrl: "/App/Ai");
        AppTestData.AssertRedirect(r, "/App/Ai");
        Assert.DoesNotContain("evil", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Over_budget_customers_see_the_quota_message()
    {
        var cid = await f.AddCustomerAsync("AI Budget Co", planName: "Basic");
        await f.WithDbAsync(async db =>
        {
            var c = await db.Customers.Include(x => x.Plan).SingleAsync(x => x.Id == cid);
            c.AiEnabled = true;
            for (var i = 0; i < c.Plan.AiMonthlyCallLimit; i++)
                db.AiDecisions.Add(new AiDecision { CustomerId = cid, Feature = AiFeatures.IncidentInterpretation, Model = "claude-opus-5-5", Status = AiDecisionStatus.Failed, CreatedAtUtc = DateTime.UtcNow });
            db.AiDecisions.Add(new AiDecision { CustomerId = cid, Feature = AiFeatures.IncidentInterpretation, Status = AiDecisionStatus.SkippedBudget, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        const string email = "ai-budget-admin@budget.local";
        await f.AddUserAsync(cid, email, "Admin123!", Roles.CustomerAdmin);
        var admin = await f.LoginAsync(email, "Admin123!");

        var html = await admin.GetHtmlAsync("/App/Ai");
        Assert.Contains("id=\"ai-over-budget\"", html);
        Assert.Contains("Incident summaries and likely causes", html);
    }

    [Fact]
    public async Task SuperAdmin_AI_page_shows_status_and_usage_per_customer()
    {
        var cid = await f.DemoCustomerIdAsync();
        await AddDecisionAsync(cid);
        var sa = await f.LoginSuperAdminAsync();

        var html = await sa.GetHtmlAsync("/SuperAdmin/Ai");
        Assert.Contains("id=\"ai-global-switch\"", html);
        Assert.Contains("claude-opus-5-5", html);
        Assert.Contains($"data-customer-id=\"{cid}\"", html);
        Assert.Contains("Demo Company", html);
        Assert.Matches(@"id=""ai-total-cost"">\$0\.\d{4}", html);
        Assert.Contains("href=\"/SuperAdmin/Ai\"", html);

        var customer = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        AppTestData.AssertAccessDenied(await customer.GetAsync("/SuperAdmin/Ai"));
    }

    [Fact]
    public async Task SuperAdmin_edits_plan_AI_quota_and_features()
    {
        var sa = await f.LoginSuperAdminAsync();
        var form = new Dictionary<string, string>
        {
            ["Input.Name"] = "AI Plan", ["Input.MinIntervalSeconds"] = "60", ["Input.MaxMonitors"] = "5",
            ["Input.RetentionDays"] = "30", ["Input.MonthlyPrice"] = "9", ["Input.IsActive"] = "true",
            ["Input.AiMonthlyCallLimit"] = "123", ["Input.AiMonthlyTokenLimit"] = "456000",
        };
        var content = new List<KeyValuePair<string, string>>(form)
        {
            new("Input.AiFeatures", AiFeatures.IncidentInterpretation),
            new("Input.AiFeatures", AiFeatures.QuestionAnswer),
            new("Input.AiFeatures", "Bogus"),
        };
        var page = await sa.GetStringAsync("/SuperAdmin/Plans/Create");
        Assert.Contains("id=\"plan-ai\"", page);
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        content.Add(new("__RequestVerificationToken", WebUtility.HtmlDecode(token)));
        var r = await sa.PostAsync("/SuperAdmin/Plans/Create", new FormUrlEncodedContent(content));
        AppTestData.AssertRedirect(r, "/SuperAdmin/Plans");

        var plan = await f.WithDbAsync(db => db.Plans.AsNoTracking().SingleAsync(p => p.Name == "AI Plan"));
        Assert.Equal((123, 456000L), (plan.AiMonthlyCallLimit, plan.AiMonthlyTokenLimit));
        Assert.Equal("IncidentInterpretation,QuestionAnswer", plan.AiFeatures);

        var edit = await sa.GetHtmlAsync($"/SuperAdmin/Plans/Edit/{plan.Id}");
        Assert.Contains("value=\"QuestionAnswer\" id=\"ai-feature-QuestionAnswer\" checked", edit.Replace("checked=\"checked\"", "checked"));
        Assert.Contains("value=\"123\"", edit);
    }

    [Fact]
    public async Task SuperAdmin_created_customers_are_opted_in()
    {
        var sa = await f.LoginSuperAdminAsync();
        var planId = await f.PlanIdAsync("Standard");
        var r = await sa.PostFormAsync("/SuperAdmin/Customers/Create", new()
        {
            ["Input.Name"] = "AI New Co", ["Input.ContactEmail"] = "contact@ainew.local", ["Input.PlanId"] = planId.ToString(),
            ["Input.AdminFullName"] = "New Admin", ["Input.AdminEmail"] = "admin@ainew.local", ["Input.AdminPassword"] = "Admin123!",
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.True(await f.WithDbAsync(db => db.Customers.Where(c => c.Name == "AI New Co").Select(c => c.AiEnabled).SingleAsync()));
    }

    [Fact]
    public async Task Deleting_a_customer_removes_its_AI_records()
    {
        var cid = await f.AddCustomerAsync("AI Cascade Co");
        await AddDecisionAsync(cid);
        await f.WithDbAsync(async (AppDbContext db) =>
        {
            db.Customers.Remove(await db.Customers.SingleAsync(c => c.Id == cid));
            await db.SaveChangesAsync();
        });
        Assert.Equal(0, await f.WithDbAsync(db => db.AiDecisions.CountAsync(d => d.CustomerId == cid)));
    }
}
