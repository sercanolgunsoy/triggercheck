namespace TriggerCheck.ProbeAgent;

/// <summary>Agent settings (section <c>Agent</c>, e.g. environment variables <c>Agent__CentralUrl</c>). See docs/DEPLOYMENT.md.</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Base URL of the main app, e.g. <c>https://triggercheck.example.com</c>.</summary>
    public string CentralUrl { get; set; } = "";
    /// <summary>Region code this agent serves (as created under SuperAdmin &gt; Regions), e.g. <c>eu-central</c>.</summary>
    public string RegionId { get; set; } = "";
    /// <summary>Agent credential of the region (<c>tcr_...</c>), shown once when the region is created or rotated.</summary>
    public string Credential { get; set; } = "";
    /// <summary>How often the agent asks for due probes.</summary>
    public int PollIntervalSeconds { get; set; } = 2;
    /// <summary>Probes running at the same time (also the lease size).</summary>
    public int MaxConcurrency { get; set; } = 20;
    /// <summary>Optional client certificate (PFX) for mutual TLS at a reverse proxy in front of the main app.</summary>
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    /// <summary>Allow a plain http CentralUrl (the credential would travel unencrypted). Development only.</summary>
    public bool AllowInsecureCentral { get; set; }

    public IEnumerable<string> Validate()
    {
        if (!Uri.TryCreate(CentralUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            yield return "Agent:CentralUrl must be an absolute http(s) URL.";
        else if (url.Scheme == Uri.UriSchemeHttp && !AllowInsecureCentral)
            yield return "Agent:CentralUrl must use https (set Agent:AllowInsecureCentral=true for local development only).";
        if (string.IsNullOrWhiteSpace(RegionId)) yield return "Agent:RegionId is required.";
        if (string.IsNullOrWhiteSpace(Credential)) yield return "Agent:Credential is required.";
        if (PollIntervalSeconds is < 1 or > 60) yield return "Agent:PollIntervalSeconds must be between 1 and 60.";
        if (MaxConcurrency is < 1 or > 100) yield return "Agent:MaxConcurrency must be between 1 and 100.";
        if (!string.IsNullOrEmpty(ClientCertificatePath) && !File.Exists(ClientCertificatePath))
            yield return "Agent:ClientCertificatePath does not exist.";
    }
}
