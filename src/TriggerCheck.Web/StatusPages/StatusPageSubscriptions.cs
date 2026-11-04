using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.StatusPages;

/// <summary>
/// E-mail subscriptions of status pages (FEAT-4): double opt-in and one-click unsubscribe. Links carry the subscriber
/// id protected with ASP.NET Core Data Protection (authenticated encryption; the confirmation link expires with the
/// unconfirmed row after <see cref="UnconfirmedDays"/> days). Subscribing never reveals whether an address is already
/// subscribed: every request gets the same answer.
/// Spam guards for confirmation e-mails (they go to unconfirmed addresses): at most one per address and page per
/// <see cref="ResendAfter"/>, <see cref="StatusPageOptions.ConfirmationsPerAddressPerDay"/> per address over all pages,
/// <see cref="StatusPageOptions.ConfirmationsPerPagePerHour"/> per page; a fixed subject, and the customer-chosen title
/// only in the body with links defanged (<see cref="Safe"/>). Only confirmed subscribers count toward
/// <see cref="MaxSubscribersPerPage"/>.
/// </summary>
public sealed partial class StatusPageSubscriptions(AppDbContext db, IDataProtectionProvider dataProtection, IAppEmailSender email,
    PublicUrls urls, TimeProvider time, IOptionsMonitor<StatusPageOptions> options, Text<SharedResource> L, ILogger<StatusPageSubscriptions> logger)
{
    public const string ConfirmPurpose = "StatusPageSubscription.Confirm.v1";
    public const string UnsubscribePurpose = "StatusPageSubscription.Unsubscribe.v1";
    public const int UnconfirmedDays = 2;
    public const int MaxSubscribersPerPage = 10_000;
    public static readonly TimeSpan ResendAfter = TimeSpan.FromHours(24);

    private ITimeLimitedDataProtector ConfirmProtector => dataProtection.CreateProtector(ConfirmPurpose).ToTimeLimitedDataProtector();
    private IDataProtector UnsubscribeProtector => dataProtection.CreateProtector(UnsubscribePurpose);

    /// <summary>Normalized address, or null when it is not a plain e-mail address.</summary>
    public static string? NormalizeEmail(string? value)
    {
        var s = (value ?? "").Trim();
        if (s.Length is 0 or > 256 || s.Any(char.IsWhiteSpace) || s.Any(char.IsControl)) return null;
        try
        {
            var a = new MailAddress(s);
            return a.Address == s && a.DisplayName.Length == 0 ? s.ToLowerInvariant() : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>Records the subscription request and sends the confirmation e-mail (when allowed). Same outcome for every case.</summary>
    public async Task SubscribeAsync(int pageId, string slug, string title, string normalizedEmail, string baseUrl, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;
        var sub = await db.StatusPageSubscribers.FirstOrDefaultAsync(s => s.StatusPageId == pageId && s.Email == normalizedEmail, ct);
        if (sub is { ConfirmedAtUtc: not null }) return;
        if (sub is not null && sub.ConfirmationSentAtUtc > now - ResendAfter) return;
        if (await db.StatusPageSubscribers.CountAsync(s => s.Email == normalizedEmail && s.ConfirmationSentAtUtc > now.AddDays(-1), ct)
            >= Math.Max(1, o.ConfirmationsPerAddressPerDay))
        {
            logger.LogInformation("Confirmation e-mail limit per address reached; subscription request ignored.");
            return;
        }
        if (await db.StatusPageSubscribers.CountAsync(s => s.StatusPageId == pageId && s.ConfirmationSentAtUtc > now.AddHours(-1), ct)
            >= Math.Max(1, o.ConfirmationsPerPagePerHour))
        {
            logger.LogWarning("Status page {PageId} reached the hourly confirmation e-mail limit; subscription request ignored.", pageId);
            return;
        }
        if (sub is null)
        {
            if (await db.StatusPageSubscribers.CountAsync(s => s.StatusPageId == pageId && s.ConfirmedAtUtc != null, ct) >= MaxSubscribersPerPage)
            {
                logger.LogWarning("Status page {PageId} reached the subscriber limit; subscription request ignored.", pageId);
                return;
            }
            sub = new StatusPageSubscriber { StatusPageId = pageId, Email = normalizedEmail, CreatedAtUtc = now };
            db.StatusPageSubscribers.Add(sub);
        }
        sub.ConfirmationSentAtUtc = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return; } // a parallel request for the same address won

        var link = $"{baseUrl}/status/{slug}/confirm?token={Uri.EscapeDataString(ConfirmToken(sub.Id))}";
        await email.SendAsync(sub.Email, L["Confirm your status page subscription"],
            string.Join("\n\n",
                L["Someone (hopefully you) asked to receive e-mail updates from the status page \"{0}\".", Safe(title)],
                L["Confirm the subscription by opening this link (valid for {0} days):", UnconfirmedDays] + "\n" + link,
                L["If you did not ask for this, ignore this e-mail; you will not receive any updates."]), ct);
    }

    [GeneratedRegex(@"[a-z][a-z0-9+.-]*://\S*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"(?<=[\w-])\.(?=[A-Za-z][A-Za-z0-9-]*\b)")]
    private static partial Regex DomainDotRx();

    /// <summary>
    /// Customer-chosen text for e-mails: one line, no control characters, at most 150 characters, URLs removed and
    /// host-like words defanged ("example.com" → "example[.]com") so mail clients do not turn them into links.
    /// </summary>
    public static string Safe(string text)
    {
        var s = new string(text.Where(c => !char.IsControl(c)).ToArray());
        s = DomainDotRx().Replace(UrlRx().Replace(s, ""), "[.]").Trim();
        return s.Length > 150 ? s[..150] : s;
    }

    /// <summary>Wall-clock expiry on purpose: the time-limited protector checks it against the system clock, not a TimeProvider.</summary>
    public string ConfirmToken(int subscriberId) =>
        ConfirmProtector.Protect(subscriberId.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddDays(UnconfirmedDays));

    public string UnsubscribeToken(int subscriberId) => UnsubscribeProtector.Protect(subscriberId.ToString(CultureInfo.InvariantCulture));

    /// <summary>True when the token is valid for a subscriber of the page with this slug (the subscriber is then confirmed).</summary>
    public async Task<bool> ConfirmAsync(string slug, string? token, CancellationToken ct)
    {
        if (Read(token, confirm: true) is not { } id) return false;
        var sub = await db.StatusPageSubscribers.FirstOrDefaultAsync(s => s.Id == id && s.StatusPage.Slug == slug, ct);
        if (sub is null) return false;
        sub.ConfirmedAtUtc ??= time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>True for a valid token of this page (also when the subscriber is already gone: unsubscribing is idempotent).</summary>
    public async Task<bool> UnsubscribeAsync(string slug, string? token, CancellationToken ct)
    {
        if (Read(token, confirm: false) is not { } id) return false;
        await db.StatusPageSubscribers.Where(s => s.Id == id && s.StatusPage.Slug == slug).ExecuteDeleteAsync(ct);
        return true;
    }

    /// <summary>Validates a token without changing anything (for the GET confirmation pages).</summary>
    public bool IsValid(string? token, bool confirm) => Read(token, confirm) is not null;

    private int? Read(string? token, bool confirm)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return null;
        try
        {
            var payload = confirm ? ConfirmProtector.Unprotect(token, out _) : UnsubscribeProtector.Unprotect(token);
            return int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>Base URL for links: Site:PublicBaseUrl, else the current request (Development).</summary>
    public string BaseUrl(HttpRequest? request) =>
        urls.BaseUrl ?? (request is null ? "" : $"{request.Scheme}://{request.Host}{request.PathBase}");

}
