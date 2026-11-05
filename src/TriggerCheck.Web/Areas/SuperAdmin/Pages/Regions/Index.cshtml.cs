using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Web.Agents;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Regions;

/// <summary>
/// FEAT-6: probe regions (one agent each). Create, rotate the agent credential, enable / disable. The credential is shown
/// once: the POST parks it for 5 minutes in a server-side one-time store (IMemoryCache, bound to the SuperAdmin and a
/// per-issue reveal id, never TempData / a cookie) and redirects; the next GET takes it out. Rotation and disabling take
/// effect on the agent's next request (its credential is checked on every request).
/// </summary>
[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db, TimeProvider time, IMemoryCache cache, IOptionsMonitor<RegionOptions> regionOptions, Text<AdminResource> L) : PageModel
{
    public const int MaxRegions = 50;

    public class CreateInput
    {
        [Required(ErrorMessage = "Region code is required."), StringLength(32, MinimumLength = 2, ErrorMessage = "The region code must be 2 to 32 characters.")]
        [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Use lower-case letters, digits and single dashes, e.g. eu-central.")]
        [Display(Name = "Region code")]
        public string Code { get; set; } = "";

        [Required(ErrorMessage = "Name is required."), StringLength(100, ErrorMessage = "Name can be at most 100 characters.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";
    }

    public record Row(int Id, string Code, string Name, bool IsEnabled, string Credential, DateTime CreatedAtUtc, DateTime? RotatedAtUtc,
        DateTime? LastSeenAtUtc, bool IsStale, int Monitors);

    [BindProperty] public CreateInput Input { get; set; } = new();
    public List<Row> Rows { get; private set; } = [];
    public string? NewCredential { get; private set; }
    public string? NewCredentialRegion { get; private set; }

    private sealed record Reveal(string RegionCode, string Token);
    private static readonly TimeSpan RevealLifetime = TimeSpan.FromMinutes(5);

    private string RevealKey(Guid revealId) => $"region-reveal:{User.FindFirstValue(ClaimTypes.NameIdentifier)}:{revealId:N}";

    public async Task OnGetAsync(Guid? reveal, CancellationToken ct)
    {
        if (reveal is Guid id && cache.TryGetValue(RevealKey(id), out Reveal? pending) && pending is not null)
        {
            cache.Remove(RevealKey(id)); // one-time
            NewCredential = pending.Token;
            NewCredentialRegion = pending.RegionCode;
            Response.Headers.CacheControl = "no-store";
        }
        await LoadAsync(ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var o = regionOptions.CurrentValue;
        var regions = await db.Regions.AsNoTracking().OrderBy(r => r.Code).ToListAsync(ct);
        var counts = await db.MonitorRegionStates.GroupBy(s => s.RegionId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        Rows = regions.Select(r => new Row(r.Id, r.Code, r.Name, r.IsEnabled, AgentTokens.Display(r.Prefix), r.CreatedAtUtc, r.CredentialRotatedAtUtc,
            r.LastSeenAtUtc, RegionQuorum.IsAgentStale(r, now, o), counts.FirstOrDefault(c => c.Key == r.Id)?.Count ?? 0)).ToList();
    }

    private IActionResult RedirectWithReveal(string code, string token)
    {
        var revealId = Guid.NewGuid();
        cache.Set(RevealKey(revealId), new Reveal(code, token), RevealLifetime);
        return RedirectToPage("Index", new { reveal = revealId });
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        Input.Code = Input.Code?.Trim() ?? "";
        Input.Name = Input.Name?.Trim() ?? "";
        if (Input.Code == RegionRules.CentralCode) ModelState.AddModelError("Input.Code", L["\"central\" is reserved for the main app."]);
        if (ModelState.IsValid && await db.Regions.AnyAsync(r => r.Code == Input.Code, ct))
            ModelState.AddModelError("Input.Code", L["A region with this code already exists."]);
        if (ModelState.IsValid && await db.Regions.CountAsync(ct) >= MaxRegions)
            ModelState.AddModelError("", L["At most {0} regions can be created.", MaxRegions]);
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        for (var attempt = 1; ; attempt++)
        {
            var generated = AgentTokens.Generate();
            db.Regions.Add(new Region
            {
                Code = Input.Code, Name = Input.Name, Prefix = generated.Prefix, SecretHash = generated.Hash, CreatedAtUtc = time.GetUtcNow().UtcDateTime
            });
            try
            {
                await db.SaveChangesAsync(ct);
                TempData["Success"] = L["Region \"{0}\" was created. Configure its agent with the credential below.", Input.Code].Value;
                return RedirectWithReveal(Input.Code, generated.Token);
            }
            catch (DbUpdateException ex) when (attempt < 3 && DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear(); // unique constraint (code raced, or prefix collision): check again
                if (await db.Regions.AnyAsync(r => r.Code == Input.Code, ct))
                {
                    ModelState.AddModelError("Input.Code", L["A region with this code already exists."]);
                    await LoadAsync(ct);
                    return Page();
                }
            }
        }
    }

    public async Task<IActionResult> OnPostRotateAsync(int id, CancellationToken ct)
    {
        var region = await db.Regions.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (region is null) return NotFound();
        var generated = AgentTokens.Generate();
        region.Prefix = generated.Prefix;
        region.SecretHash = generated.Hash;
        region.CredentialRotatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["The credential of region \"{0}\" was replaced. The old credential no longer works; update the agent.", region.Code].Value;
        return RedirectWithReveal(region.Code, generated.Token);
    }

    public async Task<IActionResult> OnPostToggleAsync(int id, CancellationToken ct)
    {
        var region = await db.Regions.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (region is null) return NotFound();
        region.IsEnabled = !region.IsEnabled;
        if (!region.IsEnabled)
        {
            // Outstanding leases end now; a late result of the disabled region is rejected (unknown item).
            await db.MonitorRegionStates.Where(s => s.RegionId == id)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.LeaseItemId, (string?)null)
                    .SetProperty(s => s.LeasedAtUtc, (DateTime?)null)
                    .SetProperty(s => s.LeasedUntilUtc, (DateTime?)null)
                    .SetProperty(s => s.LeaseFingerprint, (string?)null), ct);
        }
        await db.SaveChangesAsync(ct);
        TempData["Success"] = region.IsEnabled
            ? L["Region \"{0}\" was enabled.", region.Code].Value
            : L["Region \"{0}\" was disabled. Its agent is rejected and it no longer votes.", region.Code].Value;
        return RedirectToPage("Index");
    }
}
