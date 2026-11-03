namespace TriggerCheck.Domain;

public static class Roles
{
    /// <summary>Platform operator. Manages customers and plans. Not bound to a customer.</summary>
    public const string SuperAdmin = "SuperAdmin";
    /// <summary>Customer administrator. Manages its own customer's monitors, alert channels and users.</summary>
    public const string CustomerAdmin = "CustomerAdmin";
    /// <summary>Read-only customer member.</summary>
    public const string CustomerViewer = "CustomerViewer";
}
