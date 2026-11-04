namespace TriggerCheck.Domain.Entities;

public enum ApiKeyScope
{
    ReadOnly = 0,
    ReadWrite = 1
}

/// <summary>
/// REST API credential of a customer (tenant). The full token (<c>tc_&lt;Prefix&gt;_&lt;secret&gt;</c>) is shown once at
/// creation; only its SHA-256 hash is stored. <see cref="Prefix"/> is public and identifies the key in lists and lookups.
/// Revoked keys are kept (with <see cref="RevokedAtUtc"/>) so the list shows what existed.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public string Name { get; set; } = "";
    /// <summary>Random public part of the token, unique.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>Lower-case hex SHA-256 of the full token.</summary>
    public string SecretHash { get; set; } = "";
    public ApiKeyScope Scope { get; set; } = ApiKeyScope.ReadOnly;

    /// <summary>E-mail of the admin who created the key (display only; the user may be deleted later).</summary>
    public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    /// <summary>Last successful authentication, written at most once a minute per key.</summary>
    public DateTime? LastUsedAtUtc { get; set; }

    public bool IsRevoked => RevokedAtUtc != null;
    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc is DateTime e && e <= nowUtc;
}
