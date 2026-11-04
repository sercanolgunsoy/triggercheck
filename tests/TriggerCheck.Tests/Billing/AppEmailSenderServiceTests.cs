using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Tests.Billing;

/// <summary>Durable e-mail outbox: delivery, retry with back-off, giving up, purge and safe double stop.</summary>
public class AppEmailSenderServiceTests(AppEmailSenderServiceTests.Factory f) : IClassFixture<AppEmailSenderServiceTests.Factory>
{
    /// <summary>The background sender is removed; the tests drive <see cref="AppEmailSenderService.SendBatchAsync"/> themselves.</summary>
    public class Factory : BillingTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AppEmailSenderService)).ToList())
                    s.Remove(d);
            });
        }
    }

    private AppEmailSenderService NewSender() => ActivatorUtilities.CreateInstance<AppEmailSenderService>(f.Services);

    private async Task<long> EnqueueAsync(string to)
    {
        await f.Services.GetRequiredService<IAppEmailSender>().SendAsync(to, "Subject", "secret-token-body");
        return await f.WithDbAsync(db => db.EmailOutbox.Where(m => m.To == to).Select(m => m.Id).SingleAsync());
    }

    private Task<TriggerCheck.Domain.Entities.EmailOutbox> RowAsync(long id) => f.WithDbAsync(db => db.EmailOutbox.AsNoTracking().SingleAsync(m => m.Id == id));

    [Fact]
    public async Task Queued_mail_survives_until_sent_and_is_purged_later()
    {
        var id = await EnqueueAsync("a@outbox.test");
        Assert.Null((await RowAsync(id)).SentAtUtc); // stored, not sent: survives a restart

        await NewSender().SendBatchAsync(CancellationToken.None);
        var row = await RowAsync(id);
        Assert.NotNull(row.SentAtUtc);
        Assert.Null(row.Body); // tokens are not kept once delivered
        Assert.Equal("a@outbox.test", row.To);
        Assert.Equal(1, row.Attempts);
        Assert.Contains(f.Emails.Sent, m => m.To == "a@outbox.test");

        f.Time.Advance(TimeSpan.FromDays(8));
        await NewSender().PurgeAsync(CancellationToken.None);
        Assert.False(await f.WithDbAsync(db => db.EmailOutbox.AnyAsync(m => m.Id == id)));

        var sender = NewSender();
        await sender.StartAsync(CancellationToken.None);
        await sender.StopAsync(CancellationToken.None);
        sender.Dispose();
        await sender.StopAsync(CancellationToken.None); // second stop after Dispose: must not throw
    }

    [Fact]
    public async Task Failed_delivery_is_retried_with_back_off_and_given_up_after_five_attempts()
    {
        var id = await EnqueueAsync("b@outbox.test");
        f.Emails.FailWith = new InvalidOperationException("smtp down");
        try
        {
            var sender = NewSender();
            await sender.SendBatchAsync(CancellationToken.None);
            var row = await RowAsync(id);
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.SentAtUtc);
            Assert.NotNull(row.NextAttemptAtUtc);
            Assert.DoesNotContain("secret-token-body", row.LastError ?? "");

            await sender.SendBatchAsync(CancellationToken.None); // not due yet
            Assert.Equal(1, (await RowAsync(id)).Attempts);

            for (var i = 0; i < 4; i++)
            {
                f.Time.Advance(TimeSpan.FromHours(2));
                await sender.SendBatchAsync(CancellationToken.None);
            }
            row = await RowAsync(id);
            Assert.Equal(AppEmailSenderService.MaxAttempts, row.Attempts);
            Assert.NotNull(row.FailedAtUtc);
            Assert.Null(row.Body);
        }
        finally { f.Emails.FailWith = null; }
    }

    [Fact]
    public async Task Mail_unsent_after_a_day_is_abandoned_and_its_body_dropped()
    {
        var id = await EnqueueAsync("d@outbox.test");
        f.Emails.FailWith = new InvalidOperationException("smtp down");
        try { await NewSender().SendBatchAsync(CancellationToken.None); }
        finally { f.Emails.FailWith = null; }
        f.Time.Advance(TimeSpan.FromHours(25));
        await NewSender().PurgeAsync(CancellationToken.None);
        var row = await RowAsync(id);
        Assert.NotNull(row.FailedAtUtc);
        Assert.Null(row.Body);
        Assert.Equal("Not sent within one day", row.LastError);
    }

    [Fact]
    public async Task A_claimed_row_is_not_sent_twice()
    {
        var id = await EnqueueAsync("c@outbox.test");
        var until = f.Now.AddMinutes(2);
        await f.WithDbAsync(db => db.EmailOutbox.Where(m => m.Id == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.LockedUntilUtc, until)));
        await NewSender().SendBatchAsync(CancellationToken.None); // another instance holds it
        Assert.Null((await RowAsync(id)).SentAtUtc);
        f.Time.Advance(TimeSpan.FromMinutes(3)); // the other instance died: the claim expires
        await NewSender().SendBatchAsync(CancellationToken.None);
        Assert.NotNull((await RowAsync(id)).SentAtUtc);
    }
}
