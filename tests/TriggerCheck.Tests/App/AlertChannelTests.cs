using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.App;

public class AlertChannelTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public AlertChannelTests(TestWebAppFactory f) => _f = f;

    private Task<HttpClient> AdminAsync() => _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private Task<AlertChannel?> FindAsync(string name) =>
        _f.WithDbAsync(db => db.AlertChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name));

    private static Dictionary<string, string> Form(string name, string type, string target, bool enabled = true) => new()
    {
        ["Input.Name"] = name, ["Input.Type"] = type, ["Input.Target"] = target, ["Input.IsEnabled"] = enabled ? "true" : "false"
    };

    [Fact]
    public async Task Crud_flow()
    {
        var c = await AdminAsync();
        var cid = await _f.DemoCustomerIdAsync();

        AssertRedirect(await c.PostFormAsync("/App/Alerts/Create", Form("Ops mail", "Email", "ops@example.com")), "/App/Alerts");
        var ch = (await FindAsync("Ops mail"))!;
        Assert.Equal(cid, ch.CustomerId);
        Assert.Equal(AlertChannelType.Email, ch.Type);
        Assert.True(ch.IsEnabled);
        Assert.Contains("ops@example.com", await c.GetHtmlAsync("/App/Alerts/Index"));

        AssertRedirect(await c.PostFormAsync($"/App/Alerts/Edit/{ch.Id}", Form("Ops hook", "Webhook", "https://hooks.example.com/x")), "/App/Alerts");
        ch = (await _f.WithDbAsync(db => db.AlertChannels.AsNoTracking().FirstAsync(x => x.Id == ch.Id)));
        Assert.Equal("Ops hook", ch.Name);
        Assert.Equal(AlertChannelType.Webhook, ch.Type);
        Assert.Equal("https://hooks.example.com/x", ch.Target);

        AssertRedirect(await c.PostFormAsync($"/App/Alerts/Index?handler=Toggle&id={ch.Id}", new(), "/App/Alerts/Index"), "/App/Alerts");
        Assert.False((await FindAsync("Ops hook"))!.IsEnabled);
        AssertRedirect(await c.PostFormAsync($"/App/Alerts/Index?handler=Toggle&id={ch.Id}", new(), "/App/Alerts/Index"), "/App/Alerts");
        Assert.True((await FindAsync("Ops hook"))!.IsEnabled);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/App/Alerts/Delete/{ch.Id}")).StatusCode);
        AssertRedirect(await c.PostFormAsync($"/App/Alerts/Delete/{ch.Id}", new()), "/App/Alerts");
        Assert.Null(await FindAsync("Ops hook"));
    }

    [Theory]
    [InlineData("Email", "not-an-email")]
    [InlineData("Email", "a@b")]
    [InlineData("Email", "https://example.com")]
    [InlineData("Webhook", "example.com/hook")]
    [InlineData("Webhook", "ftp://example.com/hook")]
    [InlineData("Webhook", "ops@example.com")]
    public async Task Invalid_target_is_rejected(string type, string target)
    {
        var name = $"Bad-{Guid.NewGuid():N}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Alerts/Create", Form(name, type, target));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("field-validation-error", await r.ReadHtmlAsync());
        Assert.Null(await FindAsync(name));
    }

    [Fact]
    public async Task Can_create_disabled_channel()
    {
        AssertRedirect(await (await AdminAsync()).PostFormAsync("/App/Alerts/Create", Form("Disabled", "Email", "off@example.com", enabled: false)), "/App/Alerts");
        Assert.False((await FindAsync("Disabled"))!.IsEnabled);
    }
}
