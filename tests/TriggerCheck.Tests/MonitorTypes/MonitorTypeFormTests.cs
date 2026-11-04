using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Security;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>Create/edit forms of the Dns and JsonApi types (validation lives in MonitorInput, shared with the API).</summary>
public class MonitorTypeFormTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private Task<Domain.Entities.Monitor?> ByNameAsync(string name) =>
        f.WithDbAsync(db => db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Name == name));

    [Fact]
    public async Task Dns_monitor_is_created_with_its_fields_and_http_fields_cleared()
    {
        var c = await AdminAsync();
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("Dns MX",
            ("Type", "Dns"), ("Target", "example.com"), ("DnsRecordType", "MX"), ("AssertionOperator", "Contains"), ("ExpectedValue", " mail. "),
            ("JsonPath", "$.ignored"), ("Keyword", "ignored")));
        AssertRedirect(r, "/App/Monitors/Details/");

        var m = (await ByNameAsync("Dns MX"))!;
        Assert.Equal(MonitorType.Dns, m.Type);
        Assert.Equal(DnsRecordType.MX, m.DnsRecordType);
        Assert.Equal(AssertionOperator.Contains, m.AssertionOperator);
        Assert.Equal("mail.", m.ExpectedValue);
        Assert.Null(m.JsonPath);
        Assert.Null(m.Keyword);
        Assert.Equal(0, m.CertWarnDays);

        var html = await c.GetHtmlAsync(r.Headers.Location!.ToString());
        Assert.Contains("DNS record", html);
        Assert.Contains("MX", html);

        var list = await c.GetHtmlAsync("/App/Monitors");
        Assert.Contains("example.com (MX)", list);
    }

    [Theory]
    [InlineData("1.2.3.4", "A", null, null, "The target must be a host name without a scheme")]
    [InlineData("https://example.com", "A", null, null, "The target must be a host name without a scheme")]
    [InlineData("example.com", "", null, null, "Choose a DNS record type.")]
    [InlineData("example.com", "TXT", "Exists", "x", "A DNS monitor can only check that a record equals or contains")]
    [InlineData("", "A", null, null, "Target is required.")]
    public async Task Dns_validation(string target, string recordType, string? op, string? expected, string error)
    {
        var name = "Dns bad " + Guid.NewGuid().ToString("N")[..6];
        var fields = new List<(string, string)> { ("Type", "Dns"), ("Target", target), ("DnsRecordType", recordType) };
        if (op is not null) fields.Add(("AssertionOperator", op));
        if (expected is not null) fields.Add(("ExpectedValue", expected));
        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create", MonitorForm(name, [.. fields]));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(error, await r.ReadHtmlAsync());
        Assert.Null(await ByNameAsync(name));
    }

    [Fact]
    public async Task Json_monitor_is_created_and_editing_the_assertion_resets_the_failure_streak()
    {
        var c = await AdminAsync();
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("Json health",
            ("Type", "JsonApi"), ("Target", "https://api.example.org/health"), ("JsonPath", " $.status "), ("AssertionOperator", "Equal"), ("ExpectedValue", "ok")));
        AssertRedirect(r, "/App/Monitors/Details/");
        var m = (await ByNameAsync("Json health"))!;
        Assert.Equal("$.status", m.JsonPath);
        Assert.Equal(AssertionOperator.Equal, m.AssertionOperator);
        Assert.Equal("ok", m.ExpectedValue);
        Assert.Equal(14, m.CertWarnDays); // https JSON monitors get certificate warnings

        await f.WithDbAsync(db => db.Monitors.Where(x => x.Id == m.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsecutiveFailures, 1)));
        r = await c.PostFormAsync($"/App/Monitors/Edit/{m.Id}", MonitorForm("Json health",
            ("Type", "JsonApi"), ("Target", "https://api.example.org/health"), ("JsonPath", "$.checks[0].ms"), ("AssertionOperator", "LessThan"), ("ExpectedValue", "250")));
        AssertRedirect(r, "/App/Monitors/Details/");
        m = (await ByNameAsync("Json health"))!;
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal(AssertionOperator.LessThan, m.AssertionOperator);

        var html = await c.GetHtmlAsync($"/App/Monitors/Details/{m.Id}");
        Assert.Contains("$.checks[0].ms", html);
        Assert.Contains("is less than", html);

        // Exists drops the expected value.
        r = await c.PostFormAsync($"/App/Monitors/Edit/{m.Id}", MonitorForm("Json health",
            ("Type", "JsonApi"), ("Target", "https://api.example.org/health"), ("JsonPath", "$.status"), ("AssertionOperator", "Exists"), ("ExpectedValue", "left over")));
        AssertRedirect(r, "/App/Monitors/Details/");
        Assert.Null((await ByNameAsync("Json health"))!.ExpectedValue);
    }

    [Theory]
    [InlineData("$..status", "Equal", "ok", "Enter a JSON path like")]
    [InlineData("status", "Equal", "ok", "Enter a JSON path like")]
    [InlineData("$.a[?(@.b)]", "Equal", "ok", "Enter a JSON path like")]
    [InlineData("$.status", "", "ok", "Choose a condition.")]
    [InlineData("$.status", "Equal", "", "Enter the expected value.")]
    [InlineData("$.ms", "GreaterThan", "fast", "the expected value must be a number")]
    public async Task Json_validation(string path, string op, string expected, string error)
    {
        var name = "Json bad " + Guid.NewGuid().ToString("N")[..6];
        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create", MonitorForm(name,
            ("Type", "JsonApi"), ("Target", "https://api.example.org/"), ("JsonPath", path), ("AssertionOperator", op), ("ExpectedValue", expected)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(error, await r.ReadHtmlAsync());
        Assert.Null(await ByNameAsync(name));
    }

    [Fact]
    public async Task Create_form_lists_the_new_types_and_has_no_inline_type_script()
    {
        var html = await (await AdminAsync()).GetStringAsync("/App/Monitors/Create");
        Assert.Contains("value=\"Dns\"", html);
        Assert.Contains("value=\"Heartbeat\"", html);
        Assert.Contains("value=\"JsonApi\"", html);
        Assert.Contains("data-monitor-type", html);
        Assert.DoesNotContain("getElementById('monitor-type')", html);
    }
}

/// <summary>The SSRF form pre-check covers JsonApi but not Dns / Heartbeat (no connection to the target).</summary>
public class MonitorTypeSsrfFormTests(GuardedWebAppFactory f) : IClassFixture<GuardedWebAppFactory>
{
    private const string MonitorError = "This address points to a private or internal network and cannot be monitored.";

    [Theory]
    [InlineData("http://127.0.0.1/health", true)]
    [InlineData("http://169.254.169.254/latest", true)]
    [InlineData("https://api.example.org/health", false)]
    public async Task Json_targets_are_prechecked(string target, bool blocked)
    {
        var name = "JsonSsrf " + Guid.NewGuid().ToString("N")[..6];
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm(name,
            ("Type", "JsonApi"), ("Target", target), ("JsonPath", "$.ok"), ("AssertionOperator", "Exists"), ("IntervalSeconds", "300")));
        Assert.Equal(blocked, (await r.ReadHtmlAsync()).Contains(MonitorError));
        Assert.Equal(!blocked, await f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.Name == name)));
    }

    [Fact]
    public async Task Dns_names_are_not_prechecked()
    {
        var name = "DnsLocal " + Guid.NewGuid().ToString("N")[..6];
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm(name, ("Type", "Dns"), ("Target", "intranet.local"), ("DnsRecordType", "A"), ("IntervalSeconds", "300")));
        AssertRedirect(r, "/App/Monitors/Details/");
    }
}
