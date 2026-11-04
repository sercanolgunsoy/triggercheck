namespace TriggerCheck.Web;

// Marker types for the resource files under /Resources. Keys are the English UI text itself,
// so English needs no resource file; translations live in <Name>.tr.resx.
// Lookups go area resource first, then SharedResource (see CompositeLocalizer).

/// <summary>Strings used across the app: layout, account pages, common words, validation, Identity errors.</summary>
public sealed class SharedResource;

/// <summary>Strings of the customer panel (Areas/App).</summary>
public sealed class AppResource;

/// <summary>Strings of the SuperAdmin panel (Areas/SuperAdmin).</summary>
public sealed class AdminResource;
