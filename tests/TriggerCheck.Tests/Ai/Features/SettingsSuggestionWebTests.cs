using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai.Features;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Monitoring;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Ai.Features;

internal static class SuggestionTestData
{
    public static Task SetAiAsync(this TestWebAppFactory f, int customerId, bool enabled) =>
        f.WithDbAsync(db => db.Customers.Where(c => c.Id == customerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AiEnabled, enabled)));

    public static Task<long> AddSuggestionAsync(this TestWebAppFactory f, int customerId, int monitorId,
        SettingsSuggestionKind kind = SettingsSuggestionKind.RaiseFailureThreshold, string? current = "1", string? suggested = "2", string? evidence = null) =>
        f.WithDbAsync(async db =>
        {
            var s = new SettingsSuggestion
            {
                CustomerId = customerId, MonitorId = monitorId, Kind = kind, Status = SettingsSuggestionStatus.Open,
                CurrentValue = current, SuggestedValue = suggested, CreatedAtUtc = DateTime.UtcNow,
                EvidenceJson = evidence ?? SuggestionFact.Serialize(
                [
                    new("window_days", "7"), new("incidents", "15"), new("brief_incidents", "14"), new("longer_incidents", "1"),
                    new("longest_brief_incident_seconds", "95"), new("interval_seconds", "300"), new("failure_threshold", current ?? ""),
                    new("suggested_failure_threshold", suggested ?? ""),
                ])
            };
            db.SettingsSuggestions.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });

    public static Task<SettingsSuggestion> SuggestionAsync(this TestWebAppFactory f, long id) =>
        f.WithDbAsync(db => db.SettingsSuggestions.AsNoTracking().FirstAsync(s => s.Id == id));

    public static Task<HttpResponseMessage> PostSuggestionAsync(this HttpClient c, string handler, long id, int monitorId) =>
        c.PostFormAsync($"/App/Monitors/Suggestion?handler={handler}", new() { ["suggestionId"] = id.ToString() }, $"/App/Monitors/Details/{monitorId}");
}

/// <summary>AI-5 in the customer panel: rendering with evidence, apply / dismiss, validation, roles, tenants and gating.</summary>
public class SettingsSuggestionWebTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string ViewerEmail = "suggestion-viewer@demo.local", ViewerPassword = "Viewer123!";

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private async Task<(int Cid, int Mid)> ArrangeAsync(string name, Action<Domain.Entities.Monitor>? configure = null)
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.SetAiAsync(cid, true);
        var mid = await f.AddMonitorAsync(cid, name, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; configure?.Invoke(m); });
        return (cid, mid);
    }

    [Fact]
    public async Task Admin_sees_the_evidence_and_applies_through_the_normal_edit_path()
    {
        var (_, mid) = await ArrangeAsync("Suggest apply");
        var sid = await f.AddSuggestionAsync(await f.DemoCustomerIdAsync(), mid);
        var admin = await AdminAsync();

        var html = await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}");
        Assert.Contains("id=\"settings-suggestions\"", html);
        Assert.Contains("Raise the failure threshold from 1 to 2", html);
        Assert.Contains("In the last 7 days, 14 of 15 incidents recovered at the next check (each lasted at most 95 s).", html);
        Assert.Contains("handler=Apply", html);
        Assert.Contains("handler=Dismiss", html);

        var before = (await f.GetMonitorAsync(mid))!;
        var r = await admin.PostSuggestionAsync("Apply", sid, mid);
        AssertRedirect(r, $"/App/Monitors/Details/{mid}");

        var after = (await f.GetMonitorAsync(mid))!;
        Assert.Equal(2, after.FailureThreshold);
        Assert.Equal(before.Version + 1, after.Version); // saved through MonitorCommands (Version bump)
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Target, after.Target);
        var s = await f.SuggestionAsync(sid);
        Assert.Equal(SettingsSuggestionStatus.Applied, s.Status);
        Assert.Equal(await f.UserIdAsync(TestWebAppFactory.DemoAdminEmail), s.ClosedByUserId);
        Assert.DoesNotContain($"data-suggestion-id=\"{sid}\"", await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}"));

        // Applying again: no longer open.
        await admin.PostSuggestionAsync("Apply", sid, mid);
        Assert.Equal(2, (await f.GetMonitorAsync(mid))!.FailureThreshold);
    }

    [Fact]
    public async Task Applying_does_not_ratchet_the_next_job_run()
    {
        var (cid, mid) = await ArrangeAsync("Suggest ratchet", m => m.CreatedAtUtc = DateTime.UtcNow.AddDays(-30));
        await f.WithDbAsync(async db =>
        {
            for (var i = 0; i < 14; i++)
            {
                var start = DateTime.UtcNow.AddHours(-1 - i * 5);
                db.Incidents.Add(new Incident { MonitorId = mid, StartedAtUtc = start, ResolvedAtUtc = start.AddSeconds(200), Cause = "Timeout after 10s" });
            }
            await db.SaveChangesAsync();
        });
        async Task RunJobAsync()
        {
            using var scope = f.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SettingsSuggestionJob>().RunAsync();
        }
        await RunJobAsync();
        var sid = await f.WithDbAsync(db => db.SettingsSuggestions.Where(s => s.MonitorId == mid).Select(s => s.Id).SingleAsync());

        await (await AdminAsync()).PostSuggestionAsync("Apply", sid, mid);
        Assert.NotNull((await f.GetMonitorAsync(mid))!.SettingsChangedAtUtc); // set by the normal edit path
        await RunJobAsync();

        var rows = await f.WithDbAsync(db => db.SettingsSuggestions.AsNoTracking().Where(s => s.MonitorId == mid).ToListAsync());
        Assert.Equal(SettingsSuggestionStatus.Applied, Assert.Single(rows).Status); // no 2 -> 3 from the incidents under threshold 1
        Assert.Equal(cid, rows[0].CustomerId);
    }

    [Fact]
    public async Task Turkish_ui_shows_the_suggestion_localized()
    {
        var (cid, mid) = await ArrangeAsync("Suggest tr");
        await f.AddSuggestionAsync(cid, mid);
        var admin = await AdminAsync();
        await admin.SetLanguageAsync("tr");
        var html = await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}");
        Assert.Contains("Ayar önerileri", html);
        Assert.Contains("Hata eşiğini 1 değerinden 2 değerine yükseltin", html);
        await admin.SetLanguageAsync("en");
    }

    [Fact]
    public async Task Apply_is_refused_when_the_setting_changed_since()
    {
        var (cid, mid) = await ArrangeAsync("Suggest stale");
        var sid = await f.AddSuggestionAsync(cid, mid);
        await f.WithDbAsync(db => db.Monitors.Where(m => m.Id == mid).ExecuteUpdateAsync(s => s.SetProperty(m => m.FailureThreshold, 4)));

        await (await AdminAsync()).PostSuggestionAsync("Apply", sid, mid);

        Assert.Equal(4, (await f.GetMonitorAsync(mid))!.FailureThreshold);
        Assert.Equal(SettingsSuggestionStatus.Obsolete, (await f.SuggestionAsync(sid)).Status);
    }

    [Fact]
    public async Task Apply_runs_the_monitor_validation_and_changes_nothing_when_it_fails()
    {
        // A timeout over the form's 1-60 range, and one that is not shorter than the interval (business rule).
        var (cid, mid) = await ArrangeAsync("Suggest invalid", m => { m.TimeoutSeconds = 10; m.IntervalSeconds = 60; });
        var tooLong = await f.AddSuggestionAsync(cid, mid, SettingsSuggestionKind.RaiseTimeout, "10", "90", "[]");
        var notBelowInterval = await f.AddSuggestionAsync(cid, mid, SettingsSuggestionKind.RaiseTimeout, "10", "60", "[]");
        var admin = await AdminAsync();

        await admin.PostSuggestionAsync("Apply", tooLong, mid);
        var html = await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}");
        Assert.Contains("The suggestion could not be applied: Timeout must be between 1 and 60 seconds.", html);
        await admin.PostSuggestionAsync("Apply", notBelowInterval, mid);
        Assert.Contains("The timeout must be shorter than the check interval.", await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}"));

        Assert.Equal(10, (await f.GetMonitorAsync(mid))!.TimeoutSeconds);
        Assert.Equal(SettingsSuggestionStatus.Open, (await f.SuggestionAsync(tooLong)).Status);
        Assert.Equal(SettingsSuggestionStatus.Open, (await f.SuggestionAsync(notBelowInterval)).Status);
    }

    [Fact]
    public async Task Apply_respects_the_plan_rules_of_the_whole_monitor()
    {
        // The monitor's interval is not allowed on the plan any more: applying must not save it unchecked.
        var (cid, mid) = await ArrangeAsync("Suggest plan rule", m => m.IntervalSeconds = 15);
        var sid = await f.AddSuggestionAsync(cid, mid);
        await (await AdminAsync()).PostSuggestionAsync("Apply", sid, mid);
        Assert.Equal(1, (await f.GetMonitorAsync(mid))!.FailureThreshold);
        Assert.Equal(SettingsSuggestionStatus.Open, (await f.SuggestionAsync(sid)).Status);
    }

    [Fact]
    public async Task Target_suggestion_links_to_the_edit_form_instead_of_applying()
    {
        var (cid, mid) = await ArrangeAsync("Suggest target", m => m.Status = MonitorStatus.Down);
        var sid = await f.AddSuggestionAsync(cid, mid, SettingsSuggestionKind.TargetMayBeWrong, null, null, SuggestionFact.Serialize(
            [new("first_check_at", "2026-11-01T10:00:00Z"), new("hours_without_success", "30"), new("failed_checks", "120"), new("successful_checks", "0"),
             new("top_error_checks", "120"), new(SettingsSuggestionRules.TopErrorKey, "DNS resolution failed")]));
        var admin = await AdminAsync();
        var html = await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}");
        Assert.Contains("The target may be wrong", html);
        Assert.Contains("120 checks failed over 30 hours", html);
        Assert.Contains("DNS resolution failed", html);
        Assert.Contains($"href=\"/App/Monitors/Edit/{mid}\">Edit settings", html);

        await admin.PostSuggestionAsync("Apply", sid, mid);
        Assert.Equal(SettingsSuggestionStatus.Open, (await f.SuggestionAsync(sid)).Status);
    }

    [Fact]
    public async Task Dismiss_hides_the_suggestion()
    {
        var (cid, mid) = await ArrangeAsync("Suggest dismiss");
        var sid = await f.AddSuggestionAsync(cid, mid);
        var admin = await AdminAsync();

        AssertRedirect(await admin.PostSuggestionAsync("Dismiss", sid, mid), $"/App/Monitors/Details/{mid}");

        var s = await f.SuggestionAsync(sid);
        Assert.Equal(SettingsSuggestionStatus.Dismissed, s.Status);
        Assert.NotNull(s.ClosedAtUtc);
        Assert.Equal(1, (await f.GetMonitorAsync(mid))!.FailureThreshold);
        Assert.DoesNotContain("id=\"settings-suggestions\"", await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}"));
    }

    [Fact]
    public async Task Viewer_sees_suggestions_but_cannot_apply_or_dismiss()
    {
        var (cid, mid) = await ArrangeAsync("Suggest viewer");
        var sid = await f.AddSuggestionAsync(cid, mid);
        if (!await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == ViewerEmail)))
            await f.AddUserAsync(cid, ViewerEmail, ViewerPassword, Roles.CustomerViewer);
        var viewer = await f.LoginAsync(ViewerEmail, ViewerPassword);

        var html = await viewer.GetHtmlAsync($"/App/Monitors/Details/{mid}");
        Assert.Contains("Raise the failure threshold from 1 to 2", html);
        Assert.DoesNotContain("handler=Apply", html);
        Assert.DoesNotContain("handler=Dismiss", html);

        AssertAccessDenied(await viewer.PostSuggestionAsync("Apply", sid, mid));
        AssertAccessDenied(await viewer.PostSuggestionAsync("Dismiss", sid, mid));
        Assert.Equal(1, (await f.GetMonitorAsync(mid))!.FailureThreshold);
        Assert.Equal(SettingsSuggestionStatus.Open, (await f.SuggestionAsync(sid)).Status);
    }

    [Fact]
    public async Task Another_tenants_suggestion_is_never_shown_or_changed()
    {
        var (_, mid) = await ArrangeAsync("Suggest own");
        var other = await f.AddCustomerAsync("Suggest Other Co");
        await f.SetAiAsync(other, true);
        var otherMonitor = await f.AddMonitorAsync(other, "Other monitor", m => m.FailureThreshold = 1);
        var sid = await f.AddSuggestionAsync(other, otherMonitor);
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostSuggestionAsync("Apply", sid, mid)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostSuggestionAsync("Dismiss", sid, mid)).StatusCode);
        Assert.Equal(1, (await f.GetMonitorAsync(otherMonitor))!.FailureThreshold);
        Assert.Equal(SettingsSuggestionStatus.Open, (await f.SuggestionAsync(sid)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/App/Monitors/Details/{otherMonitor}")).StatusCode);
    }

    [Fact]
    public async Task Plan_without_the_feature_or_opt_out_hides_suggestions_and_refuses_apply()
    {
        var (cid, mid) = await ArrangeAsync("Suggest gated");
        var sid = await f.AddSuggestionAsync(cid, mid);
        var admin = await AdminAsync();
        await f.SetAiAsync(cid, false);
        try
        {
            Assert.DoesNotContain("id=\"settings-suggestions\"", await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}"));
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostSuggestionAsync("Apply", sid, mid)).StatusCode);
            Assert.Equal(1, (await f.GetMonitorAsync(mid))!.FailureThreshold);
        }
        finally
        {
            await f.SetAiAsync(cid, true);
        }

        var basic = await f.AddCustomerAsync("Suggest Basic Co", planName: "Basic");
        await f.SetAiAsync(basic, true);
        Assert.False(SettingsSuggestionCommandsAllowed(await f.WithDbAsync(db => db.Customers.Include(c => c.Plan).FirstAsync(c => c.Id == basic))));
    }

    private static bool SettingsSuggestionCommandsAllowed(Customer c) => TriggerCheck.Web.Areas.App.Support.SettingsSuggestionCommands.Allowed(c);
}

/// <summary>Apply under concurrent changes: a runtime change is retried on fresh state, a concurrent edit of the setting wins.</summary>
public class SettingsSuggestionConcurrencyTests
{
    private sealed class HookedFactory(SaveHook hook) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(hook)));
        }
    }

    private static SaveHook HookOnApply() =>
        new(db => db.ChangeTracker.Entries<SettingsSuggestion>().Any(e => e.State == EntityState.Modified && e.Entity.Status == SettingsSuggestionStatus.Applied));

    private static async Task<(int Mid, long Sid)> ArrangeAsync(TestWebAppFactory f)
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.SetAiAsync(cid, true);
        var mid = await f.AddMonitorAsync(cid, "Racy suggestion", m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        return (mid, await f.AddSuggestionAsync(cid, mid));
    }

    [Fact]
    public async Task Concurrent_status_change_is_retried_and_preserved()
    {
        var hook = HookOnApply();
        using var f = new HookedFactory(hook);
        var (mid, sid) = await ArrangeAsync(f);
        hook.Action = () => f.WithDbAsync(db => db.Monitors.Where(m => m.Id == mid).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MonitorStatus.Down).SetProperty(m => m.Version, m => m.Version + 1)));
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        await admin.PostSuggestionAsync("Apply", sid, mid);

        Assert.True(hook.Fired);
        var m = (await f.GetMonitorAsync(mid))!;
        Assert.Equal(2, m.FailureThreshold);
        Assert.Equal(MonitorStatus.Down, m.Status); // the concurrent runtime change is kept
        Assert.Equal(2, m.Version);                  // concurrent bump + apply bump
        Assert.Equal(SettingsSuggestionStatus.Applied, (await f.SuggestionAsync(sid)).Status);
    }

    [Fact]
    public async Task Concurrent_edit_of_the_setting_makes_the_suggestion_stale()
    {
        var hook = HookOnApply();
        using var f = new HookedFactory(hook);
        var (mid, sid) = await ArrangeAsync(f);
        hook.Action = () => f.WithDbAsync(db => db.Monitors.Where(m => m.Id == mid).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.FailureThreshold, 4).SetProperty(m => m.Version, m => m.Version + 1)));
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        await admin.PostSuggestionAsync("Apply", sid, mid);

        Assert.True(hook.Fired);
        Assert.Equal(4, (await f.GetMonitorAsync(mid))!.FailureThreshold); // the other admin's value is not overwritten
        Assert.Equal(SettingsSuggestionStatus.Obsolete, (await f.SuggestionAsync(sid)).Status);
    }
}
