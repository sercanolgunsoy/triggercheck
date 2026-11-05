using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring.Regions;

namespace TriggerCheck.ProbeAgent;

/// <summary>The central app rejected the agent's credential (rotated, region disabled, wrong region).</summary>
public sealed class AgentRejectedException(string message) : Exception(message);

/// <summary>Client of the main app's agent API (<see cref="AgentProtocol"/>). Agent-initiated only: the agent opens no ports.</summary>
public sealed class AgentApiClient(IHttpClientFactory clients, IOptions<AgentOptions> options)
{
    public const string ClientName = "TriggerCheck.Agent.Central";

    public async Task<LeaseResponse> LeaseAsync(int maxItems, CancellationToken ct)
    {
        using var response = await SendAsync(AgentProtocol.LeasePath, new LeaseRequest { MaxItems = maxItems }, ct);
        return await response.Content.ReadFromJsonAsync<LeaseResponse>(AgentProtocol.Json, ct) ?? new LeaseResponse();
    }

    public async Task<ResultsResponse> PostResultsAsync(IReadOnlyList<ProbeResult> results, CancellationToken ct)
    {
        using var response = await SendAsync(AgentProtocol.ResultsPath, new ResultsRequest { Results = results }, ct);
        return await response.Content.ReadFromJsonAsync<ResultsResponse>(AgentProtocol.Json, ct) ?? new ResultsResponse();
    }

    private async Task<HttpResponseMessage> SendAsync<T>(string path, T body, CancellationToken ct)
    {
        var o = options.Value;
        // Relative to CentralUrl (which may carry a path base, see BaseAddress in AgentHosting).
        using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/')) { Content = JsonContent.Create(body, options: AgentProtocol.Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.Credential);
        request.Headers.Add(AgentProtocol.RegionHeader, o.RegionId);
        var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        // Never echo the credential; the status code tells the operator what to fix.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AgentRejectedException($"The main app rejected the agent credential for region '{o.RegionId}' ({(int)status}). Check Agent:RegionId / Agent:Credential, or whether the region was disabled or its credential rotated.");
        throw new HttpRequestException($"Agent API {path} answered {(int)status}.", null, status);
    }
}
