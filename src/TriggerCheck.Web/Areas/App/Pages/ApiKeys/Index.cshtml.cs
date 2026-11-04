using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Api;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.ApiKeys;

/// <summary>
/// REST API keys of the customer. Every member sees the list (never a secret); only CustomerAdmins create and revoke.
/// The full token is rendered once, in the response to the create POST, and only its hash is stored.
/// </summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, Text<AppResource> L) : PageModel
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
    /// <summary>The new token: set only in the response to a successful create.</summary>
    public string? NewToken { get; private set; }
    public string? NewTokenName { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

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
        var now = time.GetUtcNow().UtcDateTime;

        Input.Name = Input.Name?.Trim() ?? "";
        if (!Enum.IsDefined(Input.Scope)) ModelState.AddModelError("Input.Scope", L["Invalid access level."]);
        if (!ExpiryOptions.Contains(Input.ExpiresInDays)) ModelState.AddModelError("Input.ExpiresInDays", L["Invalid expiry."]);
        if (ModelState.IsValid && await db.ApiKeys.CountAsync(k => k.CustomerId == cid && k.RevokedAtUtc == null && (k.ExpiresAtUtc == null || k.ExpiresAtUtc > now), ct) >= MaxActiveKeys)
            ModelState.AddModelError("", L["You can have at most {0} active API keys. Revoke one you no longer use first.", MaxActiveKeys]);
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        var generated = ApiKeyTokens.Generate();
        db.ApiKeys.Add(new ApiKey
        {
            CustomerId = cid, Name = Input.Name, Prefix = generated.Prefix, SecretHash = generated.Hash, Scope = Input.Scope,
            CreatedBy = User.Identity?.Name, CreatedAtUtc = now,
            ExpiresAtUtc = Input.ExpiresInDays > 0 ? now.AddDays(Input.ExpiresInDays) : null
        });
        await db.SaveChangesAsync(ct);

        // Shown once: rendered directly (not via TempData, which would put the secret in a cookie) and never cached.
        NewToken = generated.Token;
        NewTokenName = Input.Name;
        Response.Headers.CacheControl = "no-store";
        ModelState.Clear();
        Input = new();
        await LoadAsync(ct);
        return Page();
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
