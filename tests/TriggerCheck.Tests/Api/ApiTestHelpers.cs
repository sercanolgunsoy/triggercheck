using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Api;

namespace TriggerCheck.Tests.Api;

public static class ApiTestHelpers
{
    /// <summary>Creates an API key directly in the database and returns the full token.</summary>
    public static async Task<string> AddApiKeyAsync(this TestWebAppFactory f, int customerId, ApiKeyScope scope = ApiKeyScope.ReadWrite,
        DateTime? expiresAtUtc = null, DateTime? revokedAtUtc = null)
    {
        var g = ApiKeyTokens.Generate();
        await f.WithDbAsync(async db =>
        {
            db.ApiKeys.Add(new ApiKey
            {
                CustomerId = customerId, Name = "test-" + g.Prefix, Prefix = g.Prefix, SecretHash = g.Hash, Scope = scope,
                ExpiresAtUtc = expiresAtUtc, RevokedAtUtc = revokedAtUtc
            });
            await db.SaveChangesAsync();
        });
        return g.Token;
    }

    /// <summary>Cookie-less client that sends the API key as a bearer token.</summary>
    public static HttpClient ApiClient(this TestWebAppFactory f, string? token)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A valid monitor body; override fields with anonymous-object style pairs.</summary>
    public static Dictionary<string, object?> MonitorBody(string name, params (string Key, object? Value)[] overrides)
    {
        var d = new Dictionary<string, object?>
        {
            ["name"] = name, ["type"] = "Http", ["target"] = "https://example.org/", ["httpMethod"] = "GET",
            ["expectedStatusCodes"] = "200-299", ["intervalSeconds"] = 300, ["timeoutSeconds"] = 10, ["failureThreshold"] = 2
        };
        foreach (var (k, v) in overrides) d[k] = v;
        return d;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient c, string url, object body) => c.PostAsJsonAsync(url, body);
    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient c, string url, object body) => c.PutAsJsonAsync(url, body);

    public static void AssertProblem(HttpResponseMessage r, System.Net.HttpStatusCode status)
    {
        Assert.Equal(status, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }
}
