using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Tests.Api;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>REST API parity for the new monitor types (same MonitorInput / MonitorCommands as the panel).</summary>
public class MonitorTypeApiTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string Monitors = "/api/v1/monitors";

    private async Task<HttpClient> ClientAsync(ApiKeyScope scope = ApiKeyScope.ReadWrite) =>
        f.ApiClient(await f.AddApiKeyAsync(await f.DemoCustomerIdAsync(), scope));

    private static string TokenOf(string pingUrl) => pingUrl[(pingUrl.LastIndexOf('/') + 1)..];

    [Fact]
    public async Task Heartbeat_create_returns_the_ping_url_once_and_never_the_hash()
    {
        var c = await ClientAsync();
        var r = await c.PostJsonAsync(Monitors, new Dictionary<string, object?>
        {
            ["name"] = "Api heartbeat", ["type"] = "Heartbeat", ["intervalSeconds"] = 3600, ["gracePeriodSeconds"] = 300, ["failureThreshold"] = 5
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var body = await r.JsonAsync();
        var pingUrl = body.GetProperty("pingUrl").GetString()!;
        Assert.Matches(@"/hb/[A-Za-z0-9_-]{43}$", pingUrl);
        Assert.Equal(1, body.GetProperty("failureThreshold").GetInt32()); // forced for heartbeats
        Assert.Equal(300, body.GetProperty("gracePeriodSeconds").GetInt32());
        Assert.Equal(TokenOf(pingUrl)[..8], body.GetProperty("heartbeatTokenPrefix").GetString());
        var id = body.GetProperty("id").GetInt32();

        var m = await f.GetMonitorAsync(id);
        Assert.Equal(HeartbeatTokens.Hash(TokenOf(pingUrl)), m!.HeartbeatTokenHash);
        Assert.True(m.NextCheckAtUtc > DateTime.UtcNow.AddSeconds(3600)); // first deadline, not "now"

        var get = await c.GetAsync($"{Monitors}/{id}");
        var text = await get.Content.ReadAsStringAsync();
        Assert.Equal(JsonValueKind.Null, (await get.JsonAsync()).GetProperty("pingUrl").ValueKind);
        Assert.DoesNotContain(TokenOf(pingUrl), text);
        Assert.DoesNotContain(m.HeartbeatTokenHash!, text);
        Assert.DoesNotContain("heartbeatTokenHash", text, StringComparison.OrdinalIgnoreCase);

        // The ping works.
        await f.CreateClient().GetAsync(new Uri(pingUrl).PathAndQuery);
        Assert.NotNull((await f.GetMonitorAsync(id))!.LastHeartbeatAtUtc);
    }

    [Fact]
    public async Task Heartbeat_token_regeneration_needs_write_scope_and_respects_tenants()
    {
        var c = await ClientAsync();
        var created = await (await c.PostJsonAsync(Monitors, new Dictionary<string, object?> { ["name"] = "Api regen", ["type"] = "Heartbeat", ["intervalSeconds"] = 300 })).JsonAsync();
        var id = created.GetProperty("id").GetInt32();
        var oldToken = TokenOf(created.GetProperty("pingUrl").GetString()!);

        var readOnly = await ClientAsync(ApiKeyScope.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync($"{Monitors}/{id}/heartbeat-token", null)).StatusCode);

        var other = await f.AddCustomerAsync("Api HB other");
        var foreign = f.ApiClient(await f.AddApiKeyAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"{Monitors}/{id}/heartbeat-token", null)).StatusCode);

        var r = await c.PostAsync($"{Monitors}/{id}/heartbeat-token", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var newToken = TokenOf((await r.JsonAsync()).GetProperty("pingUrl").GetString()!);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HeartbeatTokens.Hash(newToken), (await f.GetMonitorAsync(id))!.HeartbeatTokenHash);

        var http = await (await c.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("Api not hb"))).JsonAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"{Monitors}/{http.GetProperty("id").GetInt32()}/heartbeat-token", null)).StatusCode);
    }

    [Fact]
    public async Task Put_switching_to_heartbeat_issues_a_token_and_back_clears_it()
    {
        var c = await ClientAsync();
        var id = (await (await c.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("Api switch"))).JsonAsync()).GetProperty("id").GetInt32();

        var r = await c.PutJsonAsync($"{Monitors}/{id}", new Dictionary<string, object?> { ["name"] = "Api switch", ["type"] = "Heartbeat", ["intervalSeconds"] = 300 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull((await r.JsonAsync()).GetProperty("pingUrl").GetString());

        // A second PUT that keeps the heartbeat does not issue (or reveal) anything.
        r = await c.PutJsonAsync($"{Monitors}/{id}", new Dictionary<string, object?> { ["name"] = "Api switch 2", ["type"] = "Heartbeat", ["intervalSeconds"] = 300 });
        Assert.Equal(JsonValueKind.Null, (await r.JsonAsync()).GetProperty("pingUrl").ValueKind);

        r = await c.PutJsonAsync($"{Monitors}/{id}", ApiTestHelpers.MonitorBody("Api switch"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null((await f.GetMonitorAsync(id))!.HeartbeatTokenHash);
    }

    [Fact]
    public async Task Dns_and_json_fields_round_trip_and_are_validated()
    {
        var c = await ClientAsync();
        var r = await c.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("Api dns", ("type", "Dns"), ("target", "_dmarc.example.com"),
            ("dnsRecordType", "TXT"), ("assertionOperator", "Contains"), ("expectedValue", "v=DMARC1")));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var dns = await r.JsonAsync();
        Assert.Equal("TXT", dns.GetProperty("dnsRecordType").GetString());
        Assert.Equal("Contains", dns.GetProperty("assertionOperator").GetString());
        Assert.Equal(JsonValueKind.Null, dns.GetProperty("gracePeriodSeconds").ValueKind);

        r = await c.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("Api json", ("type", "JsonApi"), ("target", "https://api.example.org/health"),
            ("jsonPath", "$.checks[0].ms"), ("assertionOperator", "LessThan"), ("expectedValue", "250")));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("$.checks[0].ms", (await r.JsonAsync()).GetProperty("jsonPath").GetString());

        r = await c.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("Api json bad", ("type", "JsonApi"), ("jsonPath", "$..x"), ("assertionOperator", "Exists")));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        r = await c.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("Api json head", ("type", "JsonApi"), ("httpMethod", "HEAD"), ("jsonPath", "$.x"), ("assertionOperator", "Exists")));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        r = await c.PostJsonAsync(Monitors, new Dictionary<string, object?> { ["name"] = "Api hb fast", ["type"] = "Heartbeat", ["intervalSeconds"] = 30 });
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        Assert.False(await f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.Name.StartsWith("Api json bad") || m.Name == "Api hb fast")));
    }
}
