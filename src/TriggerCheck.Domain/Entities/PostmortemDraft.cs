namespace TriggerCheck.Domain.Entities;

public enum PostmortemDraftStatus
{
    /// <summary>Editable; nothing of it is public.</summary>
    Draft = 0,
    /// <summary>A CustomerAdmin approved it and its customer message was published as status page note(s). Read-only.</summary>
    Published = 1
}

/// <summary>
/// AI-8 (autonomy A2): the postmortem draft of one resolved incident. The timeline and impact are computed from the
/// database whenever the page is shown; this row holds the human-editable texts: the likely cause (internal) and the
/// customer-facing explanation. Either text may come from an AI generation (<see cref="DecisionId"/>) or be written by
/// hand. Nothing is public until a CustomerAdmin approves it: only then is <see cref="CustomerMessage"/>, exactly as
/// saved, copied into <see cref="StatusPageNote"/>s of the chosen status pages. One draft per incident.
/// </summary>
public class PostmortemDraft
{
    public const int MaxLikelyCauseLength = 2000;
    /// <summary>Same as a status page note (StatusPageRules.MaxNoteLength).</summary>
    public const int MaxCustomerMessageLength = 1000;

    public long Id { get; set; }
    public int CustomerId { get; set; }
    public int IncidentId { get; set; }
    public PostmortemDraftStatus Status { get; set; }
    /// <summary>"en" / "tr": language of the last AI generation (the customer's AI language then).</summary>
    public string Language { get; set; } = "en";
    /// <summary>The AiDecision the texts were last generated from (null: written by hand only).</summary>
    public long? DecisionId { get; set; }
    /// <summary>Cause category of the last generation (AI-2 categories, e.g. "http_5xx"); informational.</summary>
    public string? CauseCategory { get; set; }
    /// <summary>Internal: the likely cause (hypothesis), plain text. Never published.</summary>
    public string LikelyCause { get; set; } = "";
    /// <summary>Customer-facing explanation, plain text; the only part that can be published.</summary>
    public string CustomerMessage { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    /// <summary>Last user who generated or edited the texts (no FK: users may be deleted).</summary>
    public string? UpdatedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    /// <summary>The CustomerAdmin who approved and published it (no FK).</summary>
    public string? ApprovedByUserId { get; set; }
    /// <summary>Ids of the status pages it was published on, comma separated.</summary>
    public string? PublishedStatusPageIds { get; set; }
}
