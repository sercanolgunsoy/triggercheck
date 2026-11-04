using Microsoft.Extensions.Logging.Abstractions;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Tests.Billing;

/// <summary>The e-mail background sender drains on shutdown and tolerates a second stop after Dispose.</summary>
public class AppEmailSenderServiceTests
{
    private sealed class Recorder : IAppEmailTransport
    {
        public List<string> To { get; } = [];

        public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
        {
            lock (To) To.Add(to);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Queued_mail_is_delivered_and_double_stop_is_safe()
    {
        var queue = new AppEmailQueue();
        var transport = new Recorder();
        var service = new AppEmailSenderService(queue, transport, NullLogger<AppEmailSenderService>.Instance);
        var sender = new QueuedAppEmailSender(queue, NullLogger<QueuedAppEmailSender>.Instance);

        await sender.SendAsync("a@example.test", "s", "b");
        await service.StartAsync(CancellationToken.None);
        await sender.SendAsync("b@example.test", "s", "b");
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(["a@example.test", "b@example.test"], transport.To);
        Assert.Equal(0, queue.Pending);

        service.Dispose();
        await service.StopAsync(CancellationToken.None); // host disposal after StopApplication: must not throw
        await sender.SendAsync("late@example.test", "s", "b"); // queue closed: dropped and logged, never throws
        Assert.DoesNotContain("late@example.test", transport.To);
        Assert.Equal(0, queue.Pending);
    }
}
