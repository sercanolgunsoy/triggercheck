using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>
/// Shared integration-test host. Each factory instance gets its own SQLite file, seeded with
/// roles, plans, SuperAdmin (superadmin@triggercheck.local / SuperAdmin123!) and demo customer
/// (admin@demo.local / Demo123!, plan Standard = 60s). The background scheduler is disabled.
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    public const string SuperAdminEmail = "superadmin@triggercheck.local";
    public const string SuperAdminPassword = "SuperAdmin123!";
    public const string DemoAdminEmail = "admin@demo.local";
    public const string DemoAdminPassword = "Demo123!";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"triggercheck-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.UseSetting("Monitoring:Enabled", "false");
        builder.UseSetting("Seed:DemoData", "true");
    }

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Runs <paramref name="action"/> with a scoped AppDbContext (for arranging / asserting data).</summary>
    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Creates a cookie-enabled client already logged in as the given user.</summary>
    public async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = CreateBrowser();
        var resp = await client.PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        if (resp.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Login failed for {email}: {(int)resp.StatusCode}");
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }
}

public static class HttpClientFormExtensions
{
    private static readonly Regex TokenRx = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>GETs <paramref name="url"/> (or <paramref name="formPageUrl"/>) to obtain the antiforgery token, then POSTs the form.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, Dictionary<string, string> fields, string? formPageUrl = null)
    {
        var page = await client.GetStringAsync(formPageUrl ?? url);
        var m = TokenRx.Match(page);
        if (!m.Success) throw new InvalidOperationException($"No antiforgery token on {formPageUrl ?? url}");
        fields = new(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(m.Groups[1].Value) };
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }
}
