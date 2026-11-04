namespace TriggerCheck.Domain.Entities;

public enum StatusPageVisibility
{
    /// <summary>Indexable by search engines.</summary>
    Public = 0,
    /// <summary>Reachable by its address only; marked noindex.</summary>
    Unlisted = 1
}

/// <summary>
/// Public status page of a customer (FEAT-4), served anonymously at <c>/status/{Slug}</c> (and on
/// <see cref="CustomDomain"/> once verified). Shows only the selected monitors under their public display names:
/// never targets, hosts or error texts.
/// </summary>
public class StatusPage
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public string Title { get; set; } = "";
    /// <summary>Lower-case URL name, unique across all customers.</summary>
    public string Slug { get; set; } = "";
    /// <summary>Optional announcement shown at the top (plain text).</summary>
    public string? Announcement { get; set; }
    public StatusPageVisibility Visibility { get; set; } = StatusPageVisibility.Public;
    /// <summary>IANA time zone the page shows times and daily uptime bars in.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    // ---- custom domain (CNAME), Pro / Enterprise ----
    /// <summary>Lower-case host name (e.g. status.example.com); unique. Served only while verified.</summary>
    public string? CustomDomain { get; set; }
    /// <summary>Random token the customer publishes as a DNS TXT record to prove control of <see cref="CustomDomain"/>.</summary>
    public string? CustomDomainToken { get; set; }
    public DateTime? CustomDomainVerifiedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<StatusPageMonitor> Monitors { get; set; } = new List<StatusPageMonitor>();
}

/// <summary>A monitor shown on a status page under a public display name.</summary>
public class StatusPageMonitor
{
    public int Id { get; set; }
    public int StatusPageId { get; set; }
    public StatusPage StatusPage { get; set; } = null!;
    public int MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public string DisplayName { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>E-mail subscriber of a status page (double opt-in: notified only after <see cref="ConfirmedAtUtc"/> is set).</summary>
public class StatusPageSubscriber
{
    public int Id { get; set; }
    public int StatusPageId { get; set; }
    public StatusPage StatusPage { get; set; } = null!;
    /// <summary>Normalized (trimmed, lower-case) address.</summary>
    public string Email { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>When the last confirmation e-mail was sent (resends are throttled).</summary>
    public DateTime? ConfirmationSentAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
}

/// <summary>Public, customer-written update on an incident, shown on one status page (plain text).</summary>
public class StatusPageNote
{
    public int Id { get; set; }
    public int StatusPageId { get; set; }
    public StatusPage StatusPage { get; set; } = null!;
    public int IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public string Body { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// An event subscribers of a page were notified about (incident start / resolve, maintenance start). The unique
/// (page, key) pair makes notification idempotent across restarts and instances.
/// </summary>
public class StatusPageEvent
{
    public long Id { get; set; }
    public int StatusPageId { get; set; }
    /// <summary>e.g. "incident:42:start", "incident:42:resolved", "maintenance:7:638..."</summary>
    public string Key { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}
