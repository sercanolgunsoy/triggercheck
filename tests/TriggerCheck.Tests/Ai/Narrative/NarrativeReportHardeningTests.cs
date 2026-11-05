using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Narrative;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.Ai.Narrative;

/// <summary>AI-6 review fixes: slow claim holders, request cool-down and budget, the e-mail deadline clamp.</summary>
public class NarrativeReportHardeningTests(NarrativeReportsTestFactory f) : IClassFixture<NarrativeReportsTestFactory>
{
    private async Task<int> TenantAsync(string tag)
    {
        var (cid, _, _) = await f.AddTenantAsync(tag, "Pro");
        var m = await f.AddMonitorAsync(cid, "Svc " + tag, x => { x.CreatedAtUtc = Utc(2026, 1, 1); x.SlaTarget = 99.9; });
        await f.AddIncidentAsync(m, Utc(2026, 3, 10), Utc(2026, 3, 10, 2));
        await f.AddCheckAsync(m, Utc(2026, 3, 5), up: true, ms: 180);
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiEnabled, true)));
        return cid;
    }

    private async Task<int> JobAsync()
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<NarrativeReportJob>().RunAsync();
    }

    private Task<ReportNarrative?> RowAsync(int cid, string key = "2026-03") =>
        f.WithDbAsync(db => db.ReportNarratives.AsNoTracking().FirstOrDefaultAsync(r => r.CustomerId == cid && r.PeriodKey == key));

    private Task<int> DecisionsAsync(int cid) =>
        f.WithDbAsync(db => db.AiDecisions.CountAsync(d => d.CustomerId == cid && d.Feature == AiFeatures.NarrativeReport));

    [Fact]
    public async Task A_slow_holder_keeps_its_later_claims_and_nothing_is_paid_twice()
    {
        var since = f.Now;
        var first = await TenantAsync("slow-1");
        var second = await TenantAsync("slow-2");
        var calls = 0;
        var nestedCalls = -1;
        f.Fake.Responder = _ =>
        {
            calls++;
            if (calls == 1) f.Time.Advance(TimeSpan.FromMinutes(16)); // the first call is slow (not crashed)
            else if (calls == 2)
            {
                // While the holder works on the second row, another instance runs: it must not take it over.
                var before = calls;
                JobAsync().GetAwaiter().GetResult();
                nestedCalls = calls - before;
            }
            return FakeAiClient.Success(NarrativeReportTests.GoodJson());
        };

        await JobAsync();

        // The nested instance may take rows the holder had not claimed yet, but never one in progress: every period
        // was sent to the model exactly once.
        Assert.NotEqual(-1, nestedCalls); // the second instance really ran while the holder was busy
        var perPeriod = await f.WithDbAsync(db => db.AiDecisions.Where(d => d.Feature == AiFeatures.NarrativeReport && d.CreatedAtUtc >= since)
            .GroupBy(d => new { d.CustomerId, d.SubjectId }).Select(g => g.Count()).ToListAsync());
        Assert.All(perPeriod, n => Assert.Equal(1, n));
        Assert.Equal(calls, perPeriod.Count);
        foreach (var cid in new[] { first, second })
        {
            var row = await RowAsync(cid);
            Assert.Equal((ReportNarrativeStatus.Ready, 1), (row!.Status, row.Attempts));
            Assert.Equal(1, await DecisionsAsync(cid));
        }
    }

    [Fact]
    public async Task Repeated_requests_respect_the_cooldown_and_the_budget()
    {
        var cid = await TenantAsync("cooldown");
        f.Fake.Responder = _ => AiCallResult.Fail("overloaded");
        await JobAsync();
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await JobAsync(); // second (last) attempt
        var row = await RowAsync(cid);
        Assert.Equal((ReportNarrativeStatus.Failed, 2), (row!.Status, row.Attempts));

        var admin = await f.LoginAsync("admin-cooldown@t.test", Password);
        var html = await admin.GetHtmlAsync("/App/Reports?month=2026-03");
        Assert.Contains("data-narrative-state=\"failed\"", html);
        Assert.Contains("Prepare AI narrative", html);

        // Within the cool-down the request changes nothing (no reset of the attempts, no new call).
        var r = await admin.PostFormAsync("/App/Reports?handler=Narrative&month=2026-03", new(), "/App/Reports?month=2026-03");
        Assert.Contains("tried recently", await admin.GetHtmlAsync(r.Headers.Location!.ToString()));
        Assert.Equal(2, (await RowAsync(cid))!.Attempts);

        f.Time.Advance(TimeSpan.FromMinutes(61));
        await admin.PostFormAsync("/App/Reports?handler=Narrative&month=2026-03", new(), "/App/Reports?month=2026-03");
        row = await RowAsync(cid);
        Assert.Equal((ReportNarrativeStatus.Requested, 0), (row!.Status, row.Attempts));

        // Budget used up: the request is refused before anything is queued or recorded.
        await f.WithDbAsync(async db =>
        {
            var plan = new Plan { Name = "Tiny " + cid, MinIntervalSeconds = 60, MaxMonitors = 10, AiFeatures = AiFeatures.NarrativeReport, AiMonthlyCallLimit = 1 };
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            await db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.PlanId, plan.Id));
            db.AiDecisions.Add(new AiDecision
            {
                CustomerId = cid, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 1, CreatedAtUtc = f.Now
            });
            await db.SaveChangesAsync();
        });
        await JobAsync(); // the queued row meets the used-up budget: one recorded skip
        Assert.Equal(ReportNarrativeStatus.Skipped, (await RowAsync(cid))!.Status);
        var decisions = await DecisionsAsync(cid);
        f.Time.Advance(TimeSpan.FromMinutes(61));
        for (var i = 0; i < 3; i++)
            await admin.PostFormAsync("/App/Reports?handler=Narrative&month=2026-03", new(), "/App/Reports?month=2026-03");
        await JobAsync();
        Assert.Equal(decisions, await DecisionsAsync(cid));
        html = await admin.GetHtmlAsync("/App/Reports?month=2026-03");
        Assert.Contains("data-narrative-state=\"budget\"", html);
        Assert.Contains("Prepare AI narrative", html); // can be requested again once there is budget
    }

    /// <summary>
    /// The mailer's own window arithmetic: a customer in Pacific/Fiji (UTC+12) at 2026-04-02 10:00Z = 22:00 local on the
    /// 2nd. With CatchUpDays 2 the window ends at 00:00 local on the 3rd; minus two runs (2 h) that is exactly now, so
    /// the e-mail goes out although the narrative is still expected. With CatchUpDays 3 it keeps waiting.
    /// </summary>
    [Fact]
    public async Task The_mailer_clamps_the_wait_to_its_catch_up_window()
    {
        var (cid, admin, _) = await f.AddTenantAsync("fiji", "Pro");
        await f.AddMonitorAsync(cid, "Svc fiji", x => x.CreatedAtUtc = Utc(2026, 1, 1));
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.AiEnabled, true).SetProperty(c => c.MonthlyReportEmail, true).SetProperty(c => c.ReportTimeZoneId, "Pacific/Fiji")));
        using var scope = f.Services.CreateScope();
        TriggerCheck.Web.Reporting.MonthlyReportMailer Mailer(int days) => ActivatorUtilities.CreateInstance<TriggerCheck.Web.Reporting.MonthlyReportMailer>(
            scope.ServiceProvider, new StaticOptionsMonitor<TriggerCheck.Web.Reporting.ReportOptions>(new() { CatchUpDays = days, JobIntervalMinutes = 60 }));
        Task<bool> Sent() => f.WithDbAsync(db => db.MonthlyReportDeliveries.AnyAsync(d => d.CustomerId == cid));

        await Mailer(3).RunAsync(default); // deadline min(end + 3000 min, window end - 2 h) is still ahead
        Assert.False(await Sent());
        await Mailer(2).RunAsync(default); // the window end minus two runs is now: sent without the narrative
        Assert.True(await Sent());
        Assert.DoesNotContain("AI summary", (await f.WithDbAsync(db => db.EmailOutbox.FirstAsync(m => m.To == admin))).Body);
    }

    [Fact]
    public async Task The_email_never_waits_past_the_catch_up_window()
    {
        var cid = await TenantAsync("clamp");
        using var scope = f.Services.CreateScope();
        var o = Options.Create(new AiOptions { Enabled = true, Narrative = new AiNarrativeOptions { EmailWaitMinutes = 100_000 } });
        var narratives = ActivatorUtilities.CreateInstance<NarrativeReports>(scope.ServiceProvider, o);
        var monthEnd = Utc(2026, 4, 1);
        Assert.True((await narratives.ForMailAsync(cid, "2026-03", "UTC", monthEnd, f.Now.AddHours(1), default)).Wait);
        Assert.False((await narratives.ForMailAsync(cid, "2026-03", "UTC", monthEnd, f.Now, default)).Wait);
    }
}

/// <summary>AI-6 on a database without the ReportNarratives table (created before AI-6) and the startup checks.</summary>
public class NarrativeReportUpgradeTests(NarrativeReportsTestFactory f) : IClassFixture<NarrativeReportsTestFactory>
{
    [Fact]
    public async Task Reports_page_and_email_work_when_the_table_is_missing()
    {
        var (cid, admin, _) = await f.AddTenantAsync("notable", "Pro");
        var m = await f.AddMonitorAsync(cid, "Svc notable", x => x.CreatedAtUtc = Utc(2026, 1, 1));
        await f.AddCheckAsync(m, Utc(2026, 3, 5), up: true, ms: 180);
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.AiEnabled, true).SetProperty(c => c.MonthlyReportEmail, true)));
        await f.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE \"ReportNarratives\""));

        var html = await (await f.LoginAsync(admin, Password)).GetHtmlAsync("/App/Reports?month=2026-03");
        Assert.Contains("data-narrative-state=\"off\"", html);

        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TriggerCheck.Web.Reporting.MonthlyReportMailer>().RunAsync(default);
        Assert.True(await f.WithDbAsync(db => db.MonthlyReportDeliveries.AnyAsync(d => d.CustomerId == cid)));
    }

    [Fact]
    public void A_wait_longer_than_the_catch_up_window_stops_the_startup()
    {
        using var bad = f.WithWebHostBuilder(b => b.UseSetting("Ai:Narrative:EmailWaitMinutes", "10080"));
        var ex = Assert.ThrowsAny<Exception>(() => bad.CreateClient());
        Assert.Contains("EmailWaitMinutes", ex.ToString());
    }
}
