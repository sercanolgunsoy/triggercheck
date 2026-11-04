using Microsoft.Extensions.Localization;

namespace TriggerCheck.Web.Localization;

/// <summary>
/// Looks a key up in an area resource first, then in <see cref="SharedResource"/>; a missing key returns the key
/// itself (the English text). The culture is read per call (CurrentUICulture), so instances are safe to share.
/// </summary>
public class CompositeLocalizer : IStringLocalizer
{
    private readonly IStringLocalizer _area;
    private readonly IStringLocalizer? _shared;

    public CompositeLocalizer(IStringLocalizerFactory factory, Type areaResource)
    {
        _area = factory.Create(areaResource);
        _shared = areaResource == typeof(SharedResource) ? null : factory.Create(typeof(SharedResource));
    }

    public LocalizedString this[string name]
    {
        get
        {
            var s = _area[name];
            return s.ResourceNotFound && _shared is not null ? _shared[name] : s;
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var s = _area[name, arguments];
            return s.ResourceNotFound && _shared is not null ? _shared[name, arguments] : s;
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var area = _area.GetAllStrings(includeParentCultures).ToList();
        if (_shared is null) return area;
        var keys = area.Select(s => s.Name).ToHashSet();
        return area.Concat(_shared.GetAllStrings(includeParentCultures).Where(s => !keys.Contains(s.Name)));
    }

    /// <summary>Resource to use for a type, by namespace: Areas.App → App, Areas.SuperAdmin → Admin, else Shared.</summary>
    public static Type ResourceFor(Type type)
    {
        var ns = type.Namespace ?? "";
        if (ns.StartsWith("TriggerCheck.Web.Areas.App", StringComparison.Ordinal)) return typeof(AppResource);
        if (ns.StartsWith("TriggerCheck.Web.Areas.SuperAdmin", StringComparison.Ordinal)) return typeof(AdminResource);
        return typeof(SharedResource);
    }
}

/// <summary>
/// Injectable localizer for one area: <c>@inject Text&lt;AppResource&gt; L</c> in views, or a constructor
/// parameter in page models. Usage: <c>L["Save"]</c>; with arguments pass them after the key (never an interpolated key).
/// </summary>
public sealed class Text<TArea>(IStringLocalizerFactory factory) : CompositeLocalizer(factory, typeof(TArea));
