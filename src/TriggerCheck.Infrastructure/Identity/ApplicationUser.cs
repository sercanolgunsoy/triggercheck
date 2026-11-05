using Microsoft.AspNetCore.Identity;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = "";
    /// <summary>Null for SuperAdmin users. Set for CustomerAdmin / CustomerViewer users.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>When true, the user is redirected to the change-password page until a new password is set.</summary>
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
