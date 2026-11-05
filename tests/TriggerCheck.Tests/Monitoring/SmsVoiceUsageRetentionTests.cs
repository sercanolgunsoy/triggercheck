using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Retention;

namespace TriggerCheck.Tests.Monitoring;

public class SmsVoiceUsageRetentionTests
{
    [Fact]
    public async Task Old_sms_voice_usage_rows_are_removed_regardless_of_plan_retention()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var keepForever = await host.AddCustomerAsync(retentionDays: 0);
        var today = host.Now.Date;
        await host.WithDbAsync(async db =>
        {
            db.SmsVoiceUsage.AddRange(
                new SmsVoiceUsage { CustomerId = keepForever.Id, DayUtc = today, Count = 3 },
                new SmsVoiceUsage { CustomerId = keepForever.Id, DayUtc = today.AddDays(-RetentionCleaner.SmsVoiceUsageDays), Count = 1 },
                new SmsVoiceUsage { CustomerId = keepForever.Id, DayUtc = today.AddDays(-RetentionCleaner.SmsVoiceUsageDays - 1), Count = 2 },
                new SmsVoiceUsage { CustomerId = keepForever.Id, DayUtc = today.AddDays(-400), Count = 5 });
            await db.SaveChangesAsync();
        });

        var deleted = await host.InScopeAsync(sp => sp.GetRequiredService<RetentionCleaner>().CleanupAsync());

        Assert.Equal(2, deleted);
        var left = await host.WithDbAsync(db => db.SmsVoiceUsage.OrderBy(u => u.DayUtc).Select(u => u.DayUtc).ToListAsync());
        Assert.Equal([today.AddDays(-RetentionCleaner.SmsVoiceUsageDays), today], left);
    }
}
