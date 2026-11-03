namespace TriggerCheck.Web.Infrastructure;

public static class Policies
{
    public const string SuperAdmin = "SuperAdminOnly";
    /// <summary>Any user of a customer (admin or viewer).</summary>
    public const string Customer = "CustomerMember";
    /// <summary>Customer admins only — use on pages/handlers that modify data in Areas/App.</summary>
    public const string CustomerAdmin = "CustomerAdminOnly";
}
