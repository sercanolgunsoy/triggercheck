namespace TriggerCheck.Domain.Entities;

/// <summary>
/// Durable queue of the web app's transactional e-mails (sign-up, password reset, billing notices). Requests only
/// insert a row; the background sender delivers it with retries. Sent rows are purged after a few days.
/// </summary>
public class EmailOutbox
{
    public long Id { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Plain text; may contain account tokens (never logged).</summary>
    public string Body { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public int Attempts { get; set; }
    /// <summary>Earliest time of the next attempt (retry back-off).</summary>
    public DateTime? NextAttemptAtUtc { get; set; }
    /// <summary>Claimed by a sender until this time (several app instances never send the same row twice at once).</summary>
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    /// <summary>Given up after the maximum number of attempts.</summary>
    public DateTime? FailedAtUtc { get; set; }
    /// <summary>Short error text of the last attempt (no message content).</summary>
    public string? LastError { get; set; }
}
