using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Infrastructure;

public static class AppClaims
{
    public const string CustomerId = "customer_id";
    public const string FullName = "full_name";
    /// <summary>Present while the user must change the password (see MustChangePasswordFilter).</summary>
    public const string MustChangePassword = "must_change_password";
}

/// <summary>
/// Tenant resolution for the customer panel. EVERY query in Areas/App must be filtered with <see cref="CustomerId"/>.
/// </summary>
public interface ICurrentCustomer
{
    /// <summary>Customer id of the logged-in user, or null (SuperAdmin / anonymous).</summary>
    int? CustomerId { get; }
    /// <summary>Same as CustomerId but throws if missing. Use inside Areas/App.</summary>
    int RequiredCustomerId { get; }
    /// <summary>Loads the customer including its Plan.</summary>
    Task<Customer> GetCustomerAsync(CancellationToken ct = default);
}

public class CurrentCustomer(IHttpContextAccessor http, AppDbContext db) : ICurrentCustomer
{
    private Customer? _cached;

    public int? CustomerId =>
        int.TryParse(http.HttpContext?.User.FindFirstValue(AppClaims.CustomerId), out var id) ? id : null;

    public int RequiredCustomerId => CustomerId ?? throw new UnauthorizedAccessException("User is not bound to a customer.");

    public async Task<Customer> GetCustomerAsync(CancellationToken ct = default) =>
        _cached ??= await db.Customers.Include(c => c.Plan).FirstAsync(c => c.Id == RequiredCustomerId, ct);
}

/// <summary>Adds customer_id, full_name and (when pending) must_change_password claims to the auth cookie.</summary>
public class AppClaimsPrincipalFactory(UserManager<ApplicationUser> um, RoleManager<IdentityRole> rm, IOptions<IdentityOptions> o)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(um, rm, o)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        if (user.CustomerId is int cid) id.AddClaim(new Claim(AppClaims.CustomerId, cid.ToString()));
        id.AddClaim(new Claim(AppClaims.FullName, string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? "" : user.FullName));
        if (user.MustChangePassword) id.AddClaim(new Claim(AppClaims.MustChangePassword, "true"));
        return id;
    }
}
