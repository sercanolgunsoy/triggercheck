using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.App;

/// <summary>The customer panel in Turkish (culture cookie "tr"): same pages, Turkish text and formatting.</summary>
public class TurkishUiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public TurkishUiTests(TestWebAppFactory f) => _f = f;

    private async Task<HttpClient> TurkishAdminAsync()
    {
        var c = await _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        await c.SetLanguageAsync("tr");
        return c;
    }

    [Fact]
    public async Task Monitor_list_is_in_turkish()
    {
        await _f.AddMonitorAsync(await _f.DemoCustomerIdAsync(), "TrListed", m => m.Status = MonitorStatus.Paused);
        var html = await (await TurkishAdminAsync()).GetHtmlAsync("/App/Monitors/Index");

        Assert.Contains("<html lang=\"tr\">", html);
        Assert.Contains("TrListed", html);
        Assert.Contains("<h2 class=\"mb-0\">Monitörler</h2>", html);
        Assert.Contains("monitör kullanılıyor", html);
        Assert.Contains("saatler TSİ", html);
        Assert.Contains(">Tümü<", html);
        Assert.Contains(">Devam et<", html);   // paused monitor offers resume
        Assert.Contains(">Düzenle<", html);
        Assert.Contains("5 dk", html);         // interval
        Assert.DoesNotContain("New monitor", html);
        Assert.DoesNotContain(">Edit<", html);
    }

    [Fact]
    public async Task Monitor_details_are_in_turkish()
    {
        var id = await _f.AddMonitorAsync(await _f.DemoCustomerIdAsync(), "TrDetails", m => m.Status = MonitorStatus.Up);
        await _f.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 20; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = id, CheckedAtUtc = now.AddMinutes(-5 * i), IsUp = i % 10 != 0, ResponseTimeMs = 120 });
            db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = now.AddHours(-3), ResolvedAtUtc = now.AddHours(-3).AddMinutes(65), Cause = "HTTP 503" });
            await db.SaveChangesAsync();
        });

        var html = await (await TurkishAdminAsync()).GetHtmlAsync($"/App/Monitors/Details/{id}");
        Assert.Contains("TrDetails", html);
        Assert.Contains("Şimdi kontrol et", html);
        Assert.Contains(">Duraklat<", html);
        Assert.Contains("Kontrol aralığı", html);
        Assert.Contains("Son 24 saat uptime", html);
        Assert.Contains("%90", html);               // Turkish percent format
        Assert.Contains("1 sa 5 dk", html);         // incident duration
        Assert.Contains("Çözüldü", html);
        var chartLabel = System.Text.RegularExpressions.Regex.Match(html, @"""avg"":(""[^""]*"")").Groups[1].Value;
        Assert.Equal("Ortalama (ms)", System.Text.Json.JsonSerializer.Deserialize<string>(chartLabel)); // chart label is a JS string literal
        Assert.DoesNotContain("Check now", html);
        Assert.DoesNotContain("Last 50 checks", html);
    }

    [Fact]
    public async Task Interval_not_allowed_by_plan_shows_turkish_error()
    {
        var r = await (await TurkishAdminAsync()).PostFormAsync("/App/Monitors/Create",
            MonitorForm("TrInterval", ("IntervalSeconds", "30"), ("TimeoutSeconds", "5")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.ReadHtmlAsync();
        Assert.Contains("Bu kontrol aralığı planınızda (Standard) kullanılamaz. İzin verilen: 5 dk, 1 dk.", html);
        Assert.Contains("Kontrol aralığı</label>", html);
        Assert.Null(await _f.WithDbAsync(db => db.Monitors.FirstOrDefaultAsync(m => m.Name == "TrInterval")));
    }

    [Fact]
    public async Task Data_annotation_errors_are_in_turkish()
    {
        var r = await (await TurkishAdminAsync()).PostFormAsync("/App/Monitors/Create",
            MonitorForm("", ("TimeoutSeconds", "61"), ("IntervalSeconds", "300")));
        var html = await r.ReadHtmlAsync();
        Assert.Contains("Ad zorunludur", html);
        Assert.Contains("Zaman aşımı 1-60 saniye arasında olmalıdır", html);
    }

    [Fact]
    public async Task Success_message_is_in_turkish()
    {
        var c = await TurkishAdminAsync();
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("TrCreated"));
        AssertRedirect(r, "/App/Monitors/Details/");

        var html = await c.GetHtmlAsync(r.Headers.Location!.ToString());
        Assert.Contains("\"TrCreated\" monitörü oluşturuldu. İlk kontrol birazdan yapılacak.", html);
    }

    [Fact]
    public async Task English_is_the_default_for_the_same_pages()
    {
        var c = await _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("EnCreated"));
        var html = await c.GetHtmlAsync(r.Headers.Location!.ToString());
        Assert.Contains("Monitor \"EnCreated\" was created. The first check will run shortly.", html);
        Assert.Contains("Check now", html);
        Assert.DoesNotContain("Şimdi kontrol et", html);
    }
}
