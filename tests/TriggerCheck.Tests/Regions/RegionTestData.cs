using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Agents;

namespace TriggerCheck.Tests.Regions;

public sealed record TestRegion(int Id, string Code, string Token);

/// <summary>FEAT-6 arrange helpers shared by the region tests.</summary>
public static class RegionTestData
{
    /// <summary>Adds a region with a fresh agent credential (LastSeen = <paramref name="lastSeenUtc"/>, i.e. not stale).</summary>
    public static async Task<TestRegion> AddRegionAsync(AppDbContext db, string code, DateTime? lastSeenUtc, bool enabled = true)
    {
        var g = AgentTokens.Generate();
        var r = new Region { Code = code, Name = code.ToUpperInvariant(), Prefix = g.Prefix, SecretHash = g.Hash, IsEnabled = enabled, LastSeenAtUtc = lastSeenUtc };
        db.Regions.Add(r);
        await db.SaveChangesAsync();
        return new TestRegion(r.Id, code, g.Token);
    }

    public static async Task AssignAsync(AppDbContext db, int monitorId, params TestRegion[] regions)
    {
        foreach (var r in regions) db.MonitorRegionStates.Add(new MonitorRegionState { MonitorId = monitorId, RegionId = r.Id });
        await db.SaveChangesAsync();
    }

    public static Task SetPlanRegionsAsync(AppDbContext db, int customerId, int maxRegions) =>
        db.Plans.Where(p => p.Customers.Any(c => c.Id == customerId)).ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxRegions, maxRegions));

    public static Task TouchAsync(AppDbContext db, TestRegion region, DateTime lastSeenUtc) =>
        db.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastSeenAtUtc, lastSeenUtc));

    public static ProbeResult Up(string itemId, DateTime at, int ms = 40) =>
        new() { ItemId = itemId, CheckedAtUtc = at, IsUp = true, StatusCode = 200, ResponseTimeMs = ms };

    public static ProbeResult Down(string itemId, DateTime at, string error = "Connection refused") =>
        new() { ItemId = itemId, CheckedAtUtc = at, IsUp = false, ResponseTimeMs = 3, Error = error };

    /// <summary>Cookie-less client of the agent API authenticated as <paramref name="region"/>.</summary>
    public static HttpClient AgentClient(this TestWebAppFactory f, TestRegion region, string? token = null, string? regionHeader = null)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? region.Token);
        c.DefaultRequestHeaders.Add(AgentProtocol.RegionHeader, regionHeader ?? region.Code);
        return c;
    }

    public static async Task<LeaseResponse> LeaseAsync(this HttpClient agent, int maxItems = 10)
    {
        var r = await agent.PostAsJsonAsync(AgentProtocol.LeasePath, new LeaseRequest { MaxItems = maxItems }, AgentProtocol.Json);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<LeaseResponse>(AgentProtocol.Json))!;
    }

    public static async Task<List<ResultAck>> PostResultsAsync(this HttpClient agent, params ProbeResult[] results)
    {
        var r = await agent.PostAsJsonAsync(AgentProtocol.ResultsPath, new ResultsRequest { Results = results }, AgentProtocol.Json);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ResultsResponse>(AgentProtocol.Json))!.Results.ToList();
    }
}
