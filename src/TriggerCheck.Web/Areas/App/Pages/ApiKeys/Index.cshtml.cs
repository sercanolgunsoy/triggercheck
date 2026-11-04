using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Api;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.ApiKeys;

/// <summary>
/// REST API keys of the customer. Every member sees the list (never a secret); only CustomerAdmins create and revoke.
/// Only the token's hash is stored. The full token is shown once: the create POST parks it for 5 minutes in a server-side
/// one-time store (IMemoryCache, bound to user + customer + a per-create reveal id, never TempData / a cookie) and redirects; the following GET
/// takes it out and renders it, so refreshing neither shows it again nor creates a second key.
/// </summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, IMemoryCache cache, Text<AppResource> L) : PageModel
{
    /// <summary>Upper bound of active (not revoked, not expired) keys per customer.</summary>
    public const int MaxActiveKeys = 20;
    public static readonly int[] ExpiryOptions = [0, 30, 90, 365];

    public class CreateInput
    {
        [Required(ErrorMessage = "Name is required."), StringLength(100, ErrorMessage = "Name can be at most 100 characters.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Access")]
        public ApiKeyScope Scope { get; set; } = ApiKeyScope.ReadOnly;

        /// <summary>Days until expiry; 0 = never.</summary>
        [Display(Name = "Expires")]
        public int ExpiresInDays { get; set; } = 90;
    }

    public record Row(int Id, string Name, string Display, ApiKeyScope Scope, string? CreatedBy, DateTime CreatedAtUtc,
        DateTime? ExpiresAtUtc, DateTime? LastUsedAtUtc, DateTime? RevokedAtUtc, bool IsExpired);

    [BindProperty] public CreateInput Input { get; set; } = new();

    public List<Row> Keys { get; private set; } = [];
    public bool IsAdmin => User.IsInRole(Roles.CustomerAdmin);
    /// <summary>The new token: set only on the first GET after a successful create.</summary>
    public string? NewToken { get; private set; }
    public string? NewTokenName { get; private set; }

    private sealed record Reveal(string Name, string Token);
    private static readonly TimeSpan RevealLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Bound to the user, the customer and one create (<paramref name="revealId"/>), so two tabs never collide.</summary>
    private string RevealKey(int customerId, Guid revealId) =>
        $"api-key-reveal:{User.FindFirstValue(ClaimTypes.NameIdentifier)}:{customerId}:{revealId:N}";

    public async Task OnGetAsync(Guid? reveal, CancellationToken ct)
    {
        var key = reveal is Guid id ? RevealKey(current.RequiredCustomerId, id) : null;
        if (key is not null && cache.TryGetValue(key, out Reveal? pending) && pending is not null)
        {
            cache.Remove(key); // one-time
            NewToken = pending.Token;
            NewTokenName = pending.Name;
            Response.Headers.CacheControl = "no-store";
        }
        await LoadAsync(ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var now = time.GetUtcNow().UtcDateTime;
        var keys = await db.ApiKeys.AsNoTracking().Where(k => k.CustomerId == cid)
            .OrderBy(k => k.RevokedAtUtc != null).ThenByDescending(k => k.CreatedAtUtc).ToListAsync(ct);
        Keys = keys.Select(k => new Row(k.Id, k.Name, ApiKeyTokens.Display(k.Prefix), k.Scope, k.CreatedBy, k.CreatedAtUtc,
            k.ExpiresAtUtc, k.LastUsedAtUtc, k.RevokedAtUtc, k.IsExpired(now))).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var cid = current.RequiredCustomerId;

        Input.Name = Input.Name?.Trim() ?? "";
        if (!Enum.IsDefined(Input.Scope)) ModelState.AddModelError("Input.Scope", L["Invalid access level."]);
        if (!ExpiryOptions.Contains(Input.ExpiresInDays)) ModelState.AddModelError("Input.ExpiresInDays", L["Invalid expiry."]);
        if (ModelState.IsValid)
        {
            var (token, error) = await CreateKeyAsync(cid, ct);
            if (token is not null)
            {
                var revealId = Guid.NewGuid();
                cache.Set(RevealKey(cid, revealId), new Reveal(Input.Name, token), RevealLifetime);
                return RedirectToPage("Index", new { reveal = revealId });
            }
            ModelState.AddModelError("", error!);
        }
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>
    /// Inserts the key; returns the token or a user-facing error. The active-key cap is re-counted inside a Serializable
    /// transaction (BEGIN IMMEDIATE on SQLite, like the monitor quota), so parallel posts cannot exceed it. A prefix
    /// collision on the unique index (practically impossible) is retried with a fresh token.
    /// </summary>
    private async Task<(string? Token, string? Error)> CreateKeyAsync(int cid, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var generated = ApiKeyTokens.Generate();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var active = await db.ApiKeys.CountAsync(k => k.CustomerId == cid && k.RevokedAtUtc == null && (k.ExpiresAtUtc == null || k.ExpiresAtUtc > now), ct);
                if (active >= MaxActiveKeys)
                    return (null, L["You can have at most {0} active API keys. Revoke one you no longer use first.", MaxActiveKeys]);
                db.ApiKeys.Add(new ApiKey
                {
                    CustomerId = cid, Name = Input.Name, Prefix = generated.Prefix, SecretHash = generated.Hash, Scope = Input.Scope,
                    CreatedBy = User.Identity?.Name, CreatedAtUtc = now,
                    ExpiresAtUtc = Input.ExpiresInDays > 0 ? now.AddDays(Input.ExpiresInDays) : null
                });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return (generated.Token, null);
            }
            catch (DbUpdateException ex) when (attempt < 3 && ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                db.ChangeTracker.Clear(); // unique constraint (prefix): try again with a new token
            }
            catch (Exception ex) when (MonitorCommands.IsDatabaseBusy(ex))
            {
                db.ChangeTracker.Clear();
                return (null, L["The system is busy right now. Please try again in a moment."]);
            }
        }
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var cid = current.RequiredCustomerId;
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.CustomerId == cid, ct);
        if (key is null) return NotFound();
        if (key.RevokedAtUtc is null)
        {
            key.RevokedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        TempData["Success"] = L["API key \"{0}\" was revoked. Requests using it are rejected from now on.", key.Name].Value;
        return RedirectToPage("Index");
    }

    public string ScopeName(ApiKeyScope scope) => scope == ApiKeyScope.ReadWrite ? L["Read & write"] : L["Read-only"];

    public string ExpiryName(int days) => days switch
    {
        0 => L["Never"],
        365 => L["In 1 year"],
        _ => L["In {0} days", days]
    };
}
