using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;
using TriggerCheck.Web.StatusPages;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.StatusPages;

/// <summary>FEAT-4: double opt-in subscriptions, notifications through the outbox, unsubscribe, purge.</summary>
public class StatusPageSubscriptionTests(StatusPageTestFactory f) : IClassFixture<StatusPageTestFactory>
{
    private Task<List<EmailOutbox>> MailsAsync(string to) =>
        f.WithDbAsync(db => db.EmailOutbox.AsNoTracking().Where(m => m.To == to).OrderBy(m => m.Id).ToListAsync());

    private async Task RunNotifierAsync()
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StatusPageNotifier>().RunAsync(default);
    }

    private static string Link(string body, string path) =>
        Regex.Match(body, $@"https?://[^\s]+({Regex.Escape(path)}\?token=[^\s]+)").Groups[1].Value;

    [Fact]
    public async Task Double_opt_in_notifications_and_unsubscribe()
    {
        var (cid, _, _) = await f.AddTenantAsync("subs");
        var m = await f.AddMonitorAsync(cid, "db-primary", m => m.CreatedAtUtc = Utc(2026, 1, 1));
        await f.AddStatusPageAsync(cid, "subs-page", [m]);
        const string email = "visitor@example.org";
        var visitor = f.CreateBrowser();

        var form = await visitor.GetAsync("/status/subs-page/subscribe");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        var r = await visitor.PostFormAsync("/status/subs-page/subscribe", new() { ["Email"] = " Visitor@Example.org " });
        Assert.Contains("Thank you.", await r.ReadHtmlAsync());
        var confirmMail = Assert.Single(await MailsAsync(email));
        Assert.Contains("Title subs-page", confirmMail.Subject);

        // Repeated requests give the same answer but do not re-send (and never reveal the state).
        r = await visitor.PostFormAsync("/status/subs-page/subscribe", new() { ["Email"] = email });
        Assert.Contains("Thank you.", await r.ReadHtmlAsync());
        Assert.Single(await MailsAsync(email));

        // Unconfirmed subscribers get nothing.
        var incident = await f.AddIncidentAsync(m, f.Now.AddMinutes(-5), null);
        await RunNotifierAsync();
        Assert.Single(await MailsAsync(email));

        // GET only shows the button; POST confirms.
        var confirm = Link(confirmMail.Body, "/status/subs-page/confirm");
        Assert.NotEmpty(confirm);
        var page = await visitor.GetHtmlAsync(confirm);
        Assert.Contains("Confirm subscription", page);
        Assert.Null(await f.WithDbAsync(db => db.StatusPageSubscribers.Where(s => s.Email == email).Select(s => s.ConfirmedAtUtc).SingleAsync()));
        var token = WebUtility.UrlDecode(confirm[(confirm.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        r = await visitor.PostFormAsync("/status/subs-page/confirm", new() { ["Token"] = token }, confirm);
        Assert.Contains("Your subscription is confirmed.", await r.ReadHtmlAsync());

        // The (still recent) incident start is now announced once.
        await RunNotifierAsync();
        await RunNotifierAsync();
        var mails = await MailsAsync(email);
        Assert.Equal(2, mails.Count);
        Assert.Contains("Public 0 is down", mails[1].Subject);
        Assert.DoesNotContain("db-primary", mails[1].Body);
        var unsubscribe = Link(mails[1].Body, "/status/subs-page/unsubscribe");
        Assert.NotEmpty(unsubscribe);

        // Resolution and maintenance start.
        f.Time.Advance(TimeSpan.FromMinutes(10));
        await f.WithDbAsync(db => db.Incidents.Where(i => i.Id == incident).ExecuteUpdateAsync(u => u.SetProperty(i => i.ResolvedAtUtc, f.Now.AddMinutes(-1))));
        await f.AddMaintenanceAsync(cid, "Kernel patching", f.Now.AddMinutes(-2), f.Now.AddHours(1), m);
        await RunNotifierAsync();
        await RunNotifierAsync();
        mails = await MailsAsync(email);
        Assert.Equal(4, mails.Count);
        Assert.Contains(mails, x => x.Subject.Contains("Public 0 has recovered"));
        Assert.Contains(mails, x => x.Subject.Contains("Maintenance started: Kernel patching"));

        // Unsubscribe (POST); a tampered token is rejected.
        var unsubToken = WebUtility.UrlDecode(unsubscribe[(unsubscribe.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        r = await visitor.PostFormAsync("/status/subs-page/unsubscribe", new() { ["Token"] = unsubToken + "x" }, unsubscribe);
        Assert.Contains("This link is invalid or has expired.", await r.ReadHtmlAsync());
        r = await visitor.PostFormAsync("/status/subs-page/unsubscribe", new() { ["Token"] = unsubToken }, unsubscribe);
        Assert.Contains("You are unsubscribed", await r.ReadHtmlAsync());
        Assert.False(await f.WithDbAsync(db => db.StatusPageSubscribers.AnyAsync(s => s.Email == email)));
    }

    [Fact]
    public async Task Tokens_are_bound_to_their_page()
    {
        var (cid, _, _) = await f.AddTenantAsync("bound");
        var m = await f.AddMonitorAsync(cid, "M");
        var pageA = await f.AddStatusPageAsync(cid, "bound-a", [m]);
        await f.AddStatusPageAsync(cid, "bound-b", [m]);
        var subId = await f.WithDbAsync(async db =>
        {
            var s = new StatusPageSubscriber { StatusPageId = pageA, Email = "x@example.org", CreatedAtUtc = f.Now };
            db.StatusPageSubscribers.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        using var scope = f.Services.CreateScope();
        var subs = scope.ServiceProvider.GetRequiredService<StatusPageSubscriptions>();
        Assert.False(await subs.ConfirmAsync("bound-b", subs.ConfirmToken(subId), default));
        Assert.False(await subs.ConfirmAsync("bound-a", subs.UnsubscribeToken(subId), default)); // wrong purpose
        Assert.True(await subs.ConfirmAsync("bound-a", subs.ConfirmToken(subId), default));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b.c\r\nBcc: x@y.z")]
    [InlineData("Name <a@example.org>")]
    public async Task Invalid_addresses_are_rejected(string email)
    {
        var (cid, _, _) = await f.AddTenantAsync("inv" + Math.Abs(email.GetHashCode()));
        var m = await f.AddMonitorAsync(cid, "M");
        var slug = "inv-" + Math.Abs(email.GetHashCode());
        await f.AddStatusPageAsync(cid, slug, [m]);
        var r = await f.CreateBrowser().PostFormAsync($"/status/{slug}/subscribe", new() { ["Email"] = email });
        Assert.Contains("Enter a valid e-mail address.", await r.ReadHtmlAsync());
        Assert.False(await f.WithDbAsync(db => db.StatusPageSubscribers.AnyAsync(s => s.StatusPage.Slug == slug)));
    }

    [Fact]
    public async Task Unconfirmed_subscriptions_are_purged_after_two_days()
    {
        var (cid, _, _) = await f.AddTenantAsync("purge");
        var m = await f.AddMonitorAsync(cid, "M");
        var page = await f.AddStatusPageAsync(cid, "purge-page", [m]);
        await f.WithDbAsync(async db =>
        {
            db.StatusPageSubscribers.AddRange(
                new StatusPageSubscriber { StatusPageId = page, Email = "old@example.org", CreatedAtUtc = f.Now.AddDays(-3) },
                new StatusPageSubscriber { StatusPageId = page, Email = "new@example.org", CreatedAtUtc = f.Now.AddHours(-1) },
                new StatusPageSubscriber { StatusPageId = page, Email = "kept@example.org", CreatedAtUtc = f.Now.AddDays(-30), ConfirmedAtUtc = f.Now.AddDays(-30) });
            await db.SaveChangesAsync();
        });
        await RunNotifierAsync();
        var left = await f.WithDbAsync(db => db.StatusPageSubscribers.Where(s => s.StatusPageId == page).Select(s => s.Email).OrderBy(e => e).ToListAsync());
        Assert.Equal(["kept@example.org", "new@example.org"], left);
    }

    [Fact]
    public async Task Events_of_other_monitors_and_old_events_are_not_announced()
    {
        var (cid, _, _) = await f.AddTenantAsync("scope");
        var shown = await f.AddMonitorAsync(cid, "Shown");
        var hidden = await f.AddMonitorAsync(cid, "Hidden");
        var page = await f.AddStatusPageAsync(cid, "scope-page", [shown]);
        await f.WithDbAsync(async db =>
        {
            db.StatusPageSubscribers.Add(new StatusPageSubscriber { StatusPageId = page, Email = "scope@example.org", CreatedAtUtc = f.Now, ConfirmedAtUtc = f.Now });
            await db.SaveChangesAsync();
        });
        await f.AddIncidentAsync(hidden, f.Now.AddMinutes(-1), null);
        await f.AddIncidentAsync(shown, f.Now.AddHours(-5), f.Now.AddHours(-4)); // before the look-back window
        await RunNotifierAsync();
        Assert.Empty(await MailsAsync("scope@example.org"));
    }
}
