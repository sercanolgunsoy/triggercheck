using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Security;
using TriggerCheck.Tests.Monitoring;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>JsonApi monitors through the production probe client (guarded, timed) against a local server.</summary>
public class JsonApiMonitorTests : IAsyncLifetime
{
    private LocalHttpServer _server = null!;
    private string? _lastAccept;

    public async Task InitializeAsync()
    {
        _server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet("/health", (HttpContext ctx) =>
            {
                _lastAccept = ctx.Request.Headers.Accept.ToString();
                return Results.Json(new { status = "ok", checks = new[] { new { name = "db", ms = 12 } }, version = "2.1.0" });
            });
            app.MapGet("/degraded", () => Results.Json(new { status = "degraded" }, statusCode: 200));
            app.MapGet("/error", () => Results.Json(new { status = "ok" }, statusCode: 503));
            app.MapGet("/html", () => Results.Content("<html>maintenance</html>", "text/html"));
            app.MapGet("/huge", () => Results.Text("{\"pad\":\"" + new string('x', HttpCheckerBase.MaxBodyBytes) + "\",\"status\":\"ok\"}", "application/json"));
            app.MapPost("/post", () => Results.Json(new { method = "POST" }));
            app.MapGet("/moved", () => Results.Redirect("/health"));
        });
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static ServiceProvider Build(bool allowPrivate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMonitoringEngine(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Monitoring:AllowPrivateTargets"] = allowPrivate ? "true" : "false" }).Build());
        return services.BuildServiceProvider();
    }

    private MonitorEntity Monitor(string path, string jsonPath, AssertionOperator op, string? expected, string method = "GET", string codes = "200-299") => new()
    {
        Type = MonitorType.JsonApi, Target = _server.Url(path), JsonPath = jsonPath, AssertionOperator = op, ExpectedValue = expected,
        HttpMethod = method, ExpectedStatusCodes = codes, TimeoutSeconds = 5
    };

    private static Task<CheckOutcome> CheckAsync(ServiceProvider sp, MonitorEntity m) =>
        sp.GetServices<IMonitorChecker>().Single(c => c.Type == MonitorType.JsonApi).CheckAsync(m, CancellationToken.None);

    [Fact]
    public async Task Up_when_the_assertion_holds_with_timings_and_json_accept_header()
    {
        await using var sp = Build(allowPrivate: true);
        var o = await CheckAsync(sp, Monitor("/health", "$.status", AssertionOperator.Equal, "ok"));
        Assert.True(o.IsUp, o.Error);
        Assert.Equal(200, o.StatusCode);
        Assert.NotNull(o.Timings?.TtfbMs);
        Assert.Contains("application/json", _lastAccept);

        Assert.True((await CheckAsync(sp, Monitor("/health", "$.checks[0].ms", AssertionOperator.LessThan, "100"))).IsUp);
        Assert.True((await CheckAsync(sp, Monitor("/health", "$.version", AssertionOperator.Contains, "2."))).IsUp);
        Assert.True((await CheckAsync(sp, Monitor("/health", "$.checks[0].name", AssertionOperator.Exists, null))).IsUp);
        Assert.True((await CheckAsync(sp, Monitor("/post", "$.method", AssertionOperator.Equal, "POST", method: "POST"))).IsUp);
        Assert.True((await CheckAsync(sp, Monitor("/moved", "$.status", AssertionOperator.Equal, "ok"))).IsUp); // redirects followed
    }

    [Fact]
    public async Task Down_paths()
    {
        await using var sp = Build(allowPrivate: true);

        var wrong = await CheckAsync(sp, Monitor("/degraded", "$.status", AssertionOperator.Equal, "ok"));
        Assert.False(wrong.IsUp);
        Assert.Equal(200, wrong.StatusCode);
        Assert.Equal("JSON value at $.status is \"degraded\" (expected \"ok\")", wrong.Error);

        var missing = await CheckAsync(sp, Monitor("/health", "$.checks[3].name", AssertionOperator.Exists, null));
        Assert.Equal("JSON path $.checks[3].name not found in response", missing.Error);

        var status = await CheckAsync(sp, Monitor("/error", "$.status", AssertionOperator.Equal, "ok"));
        Assert.False(status.IsUp);
        Assert.StartsWith("Unexpected status code 503", status.Error);

        var html = await CheckAsync(sp, Monitor("/html", "$.status", AssertionOperator.Equal, "ok"));
        Assert.Equal("Response is not valid JSON", html.Error);

        var huge = await CheckAsync(sp, Monitor("/huge", "$.status", AssertionOperator.Equal, "ok"));
        Assert.False(huge.IsUp);
        Assert.Equal("Response is larger than 1 MB", huge.Error);

        var tooBig = await CheckAsync(sp, Monitor("/health", "$.checks[0].ms", AssertionOperator.GreaterThan, "100"));
        Assert.Equal("JSON value at $.checks[0].ms is 12 (expected > 100)", tooBig.Error);
    }

    [Fact]
    public async Task Bad_configuration_is_down_without_a_request()
    {
        await using var sp = Build(allowPrivate: true);
        var o = await CheckAsync(sp, Monitor("/health", "$..status", AssertionOperator.Equal, "ok"));
        Assert.False(o.IsUp);
        Assert.StartsWith("Invalid JSON path", o.Error);
        Assert.Null(_lastAccept);
    }

    [Fact]
    public async Task Ssrf_guard_applies_to_json_monitors()
    {
        await using var sp = Build(allowPrivate: false);
        var o = await CheckAsync(sp, Monitor("/health", "$.status", AssertionOperator.Equal, "ok"));
        Assert.False(o.IsUp);
        Assert.Equal(TargetGuard.BlockedMessage, o.Error);
        Assert.Null(_lastAccept); // the local server was never reached
    }

    [Fact]
    public void Https_json_monitors_track_certificates_and_settings_are_probe_settings()
    {
        var m = new MonitorEntity { Type = MonitorType.JsonApi, Target = "https://api.example.com/health", JsonPath = "$.a", AssertionOperator = AssertionOperator.Exists };
        Assert.True(MonitorStateMachine.IsHttpsMonitor(m));
        var before = ProbeSettings.Of(m);
        m.JsonPath = "$.b";
        Assert.NotEqual(before, ProbeSettings.Of(m));
        m.JsonPath = "$.a";
        m.ExpectedValue = "x";
        Assert.NotEqual(before, ProbeSettings.Of(m));
        m.ExpectedValue = null;
        m.AssertionOperator = AssertionOperator.Equal;
        Assert.NotEqual(before, ProbeSettings.Of(m));
    }
}
