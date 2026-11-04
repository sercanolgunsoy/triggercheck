using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Api;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Reports;

namespace TriggerCheck.Tests.StatusPages;

/// <summary>FEAT-4 API parity: /api/v1/status-pages.</summary>
public class StatusPageApiTests(StatusPageTestFactory f) : IClassFixture<StatusPageTestFactory>
{
    private static object Body(string slug, int monitorId, string? domain = null) => new
    {
        title = "API page", slug, announcement = "Hello", visibility = "Unlisted", timeZoneId = "Europe/Berlin",
        customDomain = domain, monitors = new[] { new { monitorId, displayName = "Checkout" } }
    };

    [Fact]
    public async Task Crud_with_validation_and_tenant_isolation()
    {
        var (cid, _, _) = await f.AddTenantAsync("api");
        var (other, _, _) = await f.AddTenantAsync("api-other");
        var m = await f.AddMonitorAsync(cid, "M");
        var foreign = await f.AddMonitorAsync(other, "F");
        var api = f.ApiClient(await f.AddApiKeyAsync(cid));

        var r = await api.PostJsonAsync("/api/v1/status-pages", Body("api-page", m, "status.api-test.example"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var created = await r.JsonAsync();
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("Unlisted", created.GetProperty("visibility").GetString());
        Assert.Equal("Checkout", created.GetProperty("monitors")[0].GetProperty("displayName").GetString());
        Assert.False(created.GetProperty("customDomainVerified").GetBoolean());
        Assert.StartsWith("_triggercheck.status.api-test.example TXT", created.GetProperty("customDomainVerificationRecord").GetString());
        Assert.EndsWith("/status/api-page", created.GetProperty("url").GetString());

        var list = await (await api.GetAsync("/api/v1/status-pages")).JsonAsync();
        Assert.Equal(1, list.GetArrayLength());

        r = await api.PostJsonAsync("/api/v1/status-pages", Body("api-page", m));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        Assert.True((await r.JsonAsync()).GetProperty("errors").TryGetProperty("slug", out _));

        r = await api.PostJsonAsync("/api/v1/status-pages", Body("api-foreign", foreign));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        Assert.True((await r.JsonAsync()).GetProperty("errors").TryGetProperty("monitors", out _));

        r = await api.PutJsonAsync($"/api/v1/status-pages/{id}", Body("api-page-2", m));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var updated = await r.JsonAsync();
        Assert.Equal("api-page-2", updated.GetProperty("slug").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("customDomain").ValueKind);

        // Other tenants' keys see nothing.
        var otherApi = f.ApiClient(await f.AddApiKeyAsync(other));
        ApiTestHelpers.AssertProblem(await otherApi.GetAsync($"/api/v1/status-pages/{id}"), HttpStatusCode.NotFound);
        ApiTestHelpers.AssertProblem(await otherApi.PutJsonAsync($"/api/v1/status-pages/{id}", Body("hijack", foreign)), HttpStatusCode.NotFound);
        ApiTestHelpers.AssertProblem(await otherApi.DeleteAsync($"/api/v1/status-pages/{id}"), HttpStatusCode.NotFound);
        Assert.Equal(0, (await (await otherApi.GetAsync("/api/v1/status-pages")).JsonAsync()).GetArrayLength());

        // Read-only keys cannot write.
        var readOnly = f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly));
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"/api/v1/status-pages/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"/api/v1/status-pages/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/status-pages/{id}")).StatusCode);
        Assert.False(await f.WithDbAsync(db => db.StatusPages.AnyAsync(p => p.Id == id)));
    }

    [Fact]
    public async Task Plan_rules_apply_to_the_api()
    {
        var (cid, _, _) = await f.AddTenantAsync("api-basic", plan: "Basic");
        var m = await f.AddMonitorAsync(cid, "M");
        var api = f.ApiClient(await f.AddApiKeyAsync(cid));

        var r = await api.PostJsonAsync("/api/v1/status-pages", Body("api-basic-1", m, "status.basic.example"));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        Assert.True((await r.JsonAsync()).GetProperty("errors").TryGetProperty("customDomain", out _));

        Assert.Equal(HttpStatusCode.Created, (await api.PostJsonAsync("/api/v1/status-pages", Body("api-basic-1", m))).StatusCode);
        ApiTestHelpers.AssertProblem(await api.PostJsonAsync("/api/v1/status-pages", Body("api-basic-2", m)), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Monitor_sla_target_round_trips()
    {
        var (cid, _, _) = await f.AddTenantAsync("api-sla");
        var api = f.ApiClient(await f.AddApiKeyAsync(cid));
        var r = await api.PostJsonAsync("/api/v1/monitors", ApiTestHelpers.MonitorBody("SLA monitor", ("slaTarget", 99.95)));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(99.95, (await r.JsonAsync()).GetProperty("slaTarget").GetDouble());

        r = await api.PostJsonAsync("/api/v1/monitors", ApiTestHelpers.MonitorBody("Bad SLA", ("slaTarget", 120)));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
    }
}
