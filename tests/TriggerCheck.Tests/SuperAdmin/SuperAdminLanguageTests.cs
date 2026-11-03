using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.SuperAdmin;

/// <summary>The SuperAdmin panel is English by default and Turkish after <c>/set-language?culture=tr</c>.</summary>
public class SuperAdminLanguageTests
{
    private static async Task<HttpClient> SuperAdminInAsync(TestWebAppFactory f, string? culture)
    {
        var sa = await f.LoginSuperAdminAsync();
        if (culture is not null) await sa.SetLanguageAsync(culture);
        return sa;
    }

    [Fact]
    public async Task Overview_is_translated_and_numbers_follow_the_language()
    {
        using var f = new TestWebAppFactory();
        await f.WithDbAsync(async db =>
        {
            var m = await db.Monitors.FirstAsync();
            var now = DateTime.UtcNow;
            for (var i = 0; i < 1234; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = now.AddSeconds(-i), IsUp = true, ResponseTimeMs = 5 });
            await db.SaveChangesAsync();
        });

        var en = WebUtility.HtmlDecode(await (await SuperAdminInAsync(f, null)).GetStringAsync("/SuperAdmin/Index"));
        Assert.Contains("Platform overview", en);
        Assert.Contains("Scheduler health", en);
        Assert.Contains("1 active", en);
        Assert.Contains("1,234", en);
        Assert.Contains("$15.00", en);
        Assert.Contains("Times: TRT (Europe/Istanbul)", en);
        Assert.DoesNotContain("Platform genel bakış", en);

        var tr = WebUtility.HtmlDecode(await (await SuperAdminInAsync(f, "tr")).GetStringAsync("/SuperAdmin/Index"));
        Assert.Contains("<html lang=\"tr\">", tr);
        Assert.Contains("Platform genel bakış", tr);
        Assert.Contains("Zamanlayıcı sağlığı", tr);
        Assert.Contains("İzleme motoru zamanında çalışıyor.", tr);
        Assert.Contains("1 aktif", tr);
        Assert.Contains("0 askıda", tr);
        Assert.Contains("1.234", tr);
        Assert.Contains("15,00 $", tr);
        Assert.Contains("Saatler: TSİ (Europe/Istanbul)", tr);
        Assert.Contains("DURAKLATILDI", tr);
        Assert.DoesNotContain("Platform overview", tr);
    }

    [Fact]
    public async Task Plan_list_formats_money_and_numbers_per_language()
    {
        using var f = new TestWebAppFactory();
        var en = WebUtility.HtmlDecode(await (await SuperAdminInAsync(f, null)).GetStringAsync("/SuperAdmin/Plans/Index"));
        Assert.Contains("$199.00", en);
        Assert.Contains(">1,000<", en);   // Enterprise max monitors
        Assert.Contains("365 days", en);
        Assert.Contains("Monthly price", en);
        Assert.Contains("Delete the plan Basic?", en);

        var tr = WebUtility.HtmlDecode(await (await SuperAdminInAsync(f, "tr")).GetStringAsync("/SuperAdmin/Plans/Index"));
        Assert.Contains("199,00 $", tr);
        Assert.Contains(">1.000<", tr);
        Assert.Contains("365 gün", tr);
        Assert.Contains("Aylık ücret", tr);
        Assert.Contains("Basic planı silinsin mi?", tr);
    }

    [Fact]
    public async Task Plan_create_validation_errors_are_Turkish()
    {
        using var f = new TestWebAppFactory();
        var sa = await SuperAdminInAsync(f, "tr");
        var r = await sa.PostFormAsync("/SuperAdmin/Plans/Create", new()
        {
            ["Input.Name"] = "standard", ["Input.MinIntervalSeconds"] = "45", ["Input.MaxMonitors"] = "0",
            ["Input.RetentionDays"] = "4000", ["Input.MonthlyPrice"] = "abc", ["Input.IsActive"] = "true"
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.BodyAsync();
        Assert.Contains("Desteklenmeyen aralık. Geçerli değerler: 5 dk, 1 dk, 30 sn, 15 sn.", body);
        Assert.Contains("Bu isimde bir plan zaten var.", body);
        Assert.Contains("Maks. monitör en az 1 olmalıdır", body);
        Assert.Contains("Saklama süresi 1 ile 3650 gün arasında olmalıdır", body);
        Assert.Contains("Geçerli bir sayı girin.", body);
        Assert.Contains("Veri saklama (gün)", body); // Display name of the label
        Assert.Contains("Oluştur", body);

        // A Turkish-formatted price is accepted in Turkish mode too.
        r = await sa.PostFormAsync("/SuperAdmin/Plans/Create", new()
        {
            ["Input.Name"] = "Starter", ["Input.MinIntervalSeconds"] = "60", ["Input.MaxMonitors"] = "5",
            ["Input.RetentionDays"] = "14", ["Input.MonthlyPrice"] = "9,90", ["Input.IsActive"] = "true"
        });
        Assert.Contains("\"Starter\" planı oluşturuldu.", await sa.FollowAsync(r));
        Assert.Equal(9.90m, await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Starter").Select(p => p.MonthlyPrice).SingleAsync()));
    }

    [Fact]
    public async Task Customer_create_required_fields_in_both_languages()
    {
        using var f = new TestWebAppFactory();
        var empty = new Dictionary<string, string>
        {
            ["Input.Name"] = "", ["Input.ContactEmail"] = "not-an-email", ["Input.PlanId"] = "",
            ["Input.AdminFullName"] = "", ["Input.AdminEmail"] = "", ["Input.AdminPassword"] = "short"
        };

        var en = await (await (await SuperAdminInAsync(f, null)).PostFormAsync("/SuperAdmin/Customers/Create", empty)).BodyAsync();
        Assert.Contains("Customer name is required.", en);
        Assert.Contains("Enter a valid e-mail.", en);
        Assert.Contains("The password must be at least 8 characters long.", en);
        Assert.Contains("Customer name", en);

        var tr = await (await (await SuperAdminInAsync(f, "tr")).PostFormAsync("/SuperAdmin/Customers/Create", empty)).BodyAsync();
        Assert.Contains("Müşteri adı zorunludur", tr);
        Assert.Contains("Geçerli bir e-posta girin", tr);
        Assert.Contains("Şifre en az 8 karakter olmalıdır", tr);
        Assert.Contains("Yönetici e-postası zorunludur", tr);
        Assert.Contains("İlk yönetici kullanıcı (CustomerAdmin)", tr);
        Assert.DoesNotContain("Customer name is required.", tr);
    }

    [Fact]
    public async Task Suspend_message_and_confirm_dialog_are_Turkish()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var sa = await SuperAdminInAsync(f, "tr");
        var url = $"/SuperAdmin/Customers/Details/{demo.Id}";

        var page = await sa.GetStringAsync(url);
        Assert.Contains("Müşteri askıya alınsın mı?", WebUtility.HtmlDecode(page));

        var html = await sa.FollowAsync(await sa.PostFormAsync($"{url}?handler=Suspend", [], url));
        Assert.Contains("\"Demo Company\" askıya alındı. Kullanıcıları giriş yapamaz ve monitörleri kontrol edilmez.", html);
        Assert.Contains("Bu müşteri askıda", html);
        Assert.Contains("Etkinleştir", html);

        html = await sa.FollowAsync(await sa.PostFormAsync($"{url}?handler=Suspend", [], url));
        Assert.Contains("Müşteri zaten askıda.", html);
    }
}
