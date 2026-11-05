using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Pages.StatusPages;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Api;

public sealed record StatusPageMonitorDto(int MonitorId, string DisplayName);

/// <param name="Url">Public address on this app (/status/{slug}).</param>
/// <param name="CustomDomainVerificationRecord">While the custom domain is not verified: the TXT record to publish ("name TXT value").</param>
public sealed record StatusPageDto(int Id, string Title, string Slug, string? Announcement, StatusPageVisibility Visibility, string TimeZoneId,
    string Url, string? CustomDomain, bool CustomDomainVerified, string? CustomDomainVerificationRecord,
    IReadOnlyList<StatusPageMonitorDto> Monitors, DateTime CreatedAtUtc)
{
    public static StatusPageDto From(StatusPage p, string baseUrl) => new(
        p.Id, p.Title, p.Slug, p.Announcement, p.Visibility, p.TimeZoneId, $"{baseUrl}/status/{p.Slug}",
        p.CustomDomain, p.CustomDomainVerifiedAtUtc is not null,
        p.CustomDomain is not null && p.CustomDomainToken is not null && p.CustomDomainVerifiedAtUtc is null
            ? $"{StatusPageRules.VerificationRecordName(p.CustomDomain)} TXT \"{StatusPageRules.VerificationRecordValue(p.CustomDomainToken)}\""
            : null,
        p.Monitors.OrderBy(m => m.SortOrder).Select(m => new StatusPageMonitorDto(m.MonitorId, m.DisplayName)).ToList(),
        Utc.Of(p.CreatedAtUtc));
}

/// <summary>Body of POST / PUT /status-pages: the panel's form model plus an optional custom domain (Pro / Enterprise).</summary>
public sealed class StatusPageRequest : StatusPageInput
{
    /// <summary>Host name such as status.example.com; null removes it. A changed domain must be verified again (in the panel).</summary>
    public string? CustomDomain { get; set; }
}

/// <summary>
/// FEAT-4 API parity: CRUD of the key's customer's status pages (read scope for GET, write scope for changes), with the
/// panel's validation and plan rules. Other tenants' ids answer 404. Custom domains are verified in the panel.
/// </summary>
public static class StatusPageApi
{
    public static void MapStatusPageApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiSetup.BasePath + "/status-pages").WithTags("Status pages")
            .RequireAuthorization(ApiSetup.ReadPolicy)
            .RequireRateLimiting(ApiSetup.RateLimitPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapGet("", ListAsync).Produces<List<StatusPageDto>>();
        group.MapGet("/{id:int}", GetAsync).Produces<StatusPageDto>().ProducesProblem(StatusCodes.Status404NotFound);
        var writes = group.MapGroup("").RequireAuthorization(ApiSetup.WritePolicy).ProducesProblem(StatusCodes.Status403Forbidden);
        writes.MapPost("", CreateAsync).Accepts<StatusPageRequest>("application/json")
            .Produces<StatusPageDto>(StatusCodes.Status201Created).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        writes.MapPut("/{id:int}", UpdateAsync).Accepts<StatusPageRequest>("application/json")
            .Produces<StatusPageDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        writes.MapDelete("/{id:int}", DeleteAsync).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, ICurrentCustomer current, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var pages = await db.StatusPages.AsNoTracking().Include(p => p.Monitors).Where(p => p.CustomerId == cid)
            .OrderBy(p => p.Title).ThenBy(p => p.Id).ToListAsync(ct);
        var baseUrl = BaseUrl(http);
        return Results.Ok(pages.Select(p => StatusPageDto.From(p, baseUrl)).ToList());
    }

    private static async Task<IResult> GetAsync(int id, HttpContext http, AppDbContext db, ICurrentCustomer current, CancellationToken ct)
    {
        var page = await FindAsync(db, current.RequiredCustomerId, id, tracked: false, ct);
        return page is null ? ApiProblems.NotFound() : Results.Ok(StatusPageDto.From(page, BaseUrl(http)));
    }

    private static async Task<IResult> CreateAsync(HttpContext http, AppDbContext db, ICurrentCustomer current, TimeProvider time,
        Text<AppResource> L, CancellationToken ct)
    {
        var (input, error) = await ApiProblems.ReadJsonAsync<StatusPageRequest>(http, ct);
        if (error is not null) return error;
        var cid = current.RequiredCustomerId;
        var plan = (await current.GetCustomerAsync(ct)).Plan;
        if (!StatusPageRules.CanAddPage(plan, await db.StatusPages.CountAsync(p => p.CustomerId == cid, ct)))
            return ApiProblems.Conflict($"You have reached the status page limit of your plan ({StatusPageRules.LimitText(plan)}).");

        var page = new StatusPage { CustomerId = cid, CreatedAtUtc = time.GetUtcNow().UtcDateTime };
        var result = await ApplyAsync(http, db, cid, plan, page, input!, L, ct);
        if (result is not null) return result;
        try
        {
            if (!await StatusPageRules.AddWithinLimitAsync(db, page, plan, ct))
                return ApiProblems.Conflict($"You have reached the status page limit of your plan ({StatusPageRules.LimitText(plan)}).");
        }
        catch (DbUpdateException) { return ApiProblems.Conflict("The address is already taken."); }
        await StatusPageSetup.EvictAsync(http.RequestServices, ct);
        return Results.Created($"{ApiSetup.BasePath}/status-pages/{page.Id}", StatusPageDto.From(page, BaseUrl(http)));
    }

    /// <summary>Full replacement (omitted fields get their defaults; omitted customDomain removes it).</summary>
    private static async Task<IResult> UpdateAsync(int id, HttpContext http, AppDbContext db, ICurrentCustomer current, Text<AppResource> L, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var page = await FindAsync(db, cid, id, tracked: true, ct);
        if (page is null) return ApiProblems.NotFound();
        var (input, error) = await ApiProblems.ReadJsonAsync<StatusPageRequest>(http, ct);
        if (error is not null) return error;
        var plan = (await current.GetCustomerAsync(ct)).Plan;
        var result = await ApplyAsync(http, db, cid, plan, page, input!, L, ct);
        if (result is not null) return result;
        if (!await TrySaveAsync(db, ct)) return ApiProblems.Conflict("The address or custom domain is already taken.");
        await StatusPageSetup.EvictAsync(http.RequestServices, ct);
        return Results.Ok(StatusPageDto.From(page, BaseUrl(http)));
    }

    private static async Task<IResult> DeleteAsync(int id, HttpContext http, AppDbContext db, ICurrentCustomer current, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var deleted = await db.StatusPages.Where(p => p.Id == id && p.CustomerId == cid).ExecuteDeleteAsync(ct);
        if (deleted == 0) return ApiProblems.NotFound();
        await StatusPageSetup.EvictAsync(http.RequestServices, ct);
        return Results.NoContent();
    }

    /// <summary>Validates and applies the request; returns a validation problem, or null on success.</summary>
    private static async Task<IResult?> ApplyAsync(HttpContext http, AppDbContext db, int cid, Plan plan, StatusPage page, StatusPageRequest input,
        Text<AppResource> L, CancellationToken ct)
    {
        var ms = new ModelStateDictionary();
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(input, new ValidationContext(input, http.RequestServices, null), results, validateAllProperties: true);
        foreach (var r in results)
            foreach (var member in r.MemberNames.DefaultIfEmpty(""))
                ms.AddModelError(member, r.ErrorMessage ?? "Invalid value.");
        input.Monitors ??= [];
        foreach (var m in input.Monitors) m.Selected = true;
        var monitors = await input.ValidateAsync(ms, L, db, cid, page.Id == 0 ? null : page.Id, ct, prefix: "");

        string? domain = null;
        if (!string.IsNullOrWhiteSpace(input.CustomDomain))
        {
            var urls = http.RequestServices.GetRequiredService<PublicUrls>();
            var appHost = Uri.TryCreate(urls.BaseUrl, UriKind.Absolute, out var u) ? u.Host : null;
            domain = StatusPageRules.NormalizeDomain(input.CustomDomain, appHost);
            if (!plan.AllowsStatusPageCustomDomain)
                ms.AddModelError(nameof(StatusPageRequest.CustomDomain), "Custom domains are available on the Pro and Enterprise plans.");
            else if (domain is null)
                ms.AddModelError(nameof(StatusPageRequest.CustomDomain), "Enter a host name such as status.example.com.");
            else if (await db.StatusPages.AnyAsync(p => p.CustomDomain == domain && p.Id != page.Id && p.CustomDomainVerifiedAtUtc != null, ct))
                ms.AddModelError(nameof(StatusPageRequest.CustomDomain), "This domain is already used by another status page.");
        }
        if (!ms.IsValid)
            return Results.ValidationProblem(ms.Where(e => e.Value is { Errors.Count: > 0 })
                .ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray()));

        input.ApplyTo(page, monitors);
        if (page.CustomDomain != domain)
        {
            page.CustomDomain = domain;
            page.CustomDomainToken = domain is null ? null : StatusPageRules.NewVerificationToken();
            page.CustomDomainVerifiedAtUtc = null;
            page.CustomDomainCheckedAtUtc = null;
            page.CustomDomainFailures = 0;
        }
        return null;
    }

    private static async Task<bool> TrySaveAsync(AppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { return false; }
    }

    private static Task<StatusPage?> FindAsync(AppDbContext db, int cid, int id, bool tracked, CancellationToken ct)
    {
        var q = db.StatusPages.Include(p => p.Monitors).Where(p => p.Id == id && p.CustomerId == cid);
        return (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private static string BaseUrl(HttpContext http) => http.RequestServices.GetRequiredService<PublicUrls>().Build(http.Request, "");
}
