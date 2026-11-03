using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;

namespace TriggerCheck.Web.Localization;

/// <summary>
/// UI languages: English (default) and Turkish. The language comes only from the culture cookie set by
/// <c>/set-language</c> (the browser's Accept-Language is deliberately ignored so the default stays English).
/// The formatting culture is always invariant: only CurrentUICulture changes per request.
/// </summary>
public static class LocalizationSetup
{
    public const string English = "en";
    public const string Turkish = "tr";
    public static readonly string[] Languages = [English, Turkish];

    public static IServiceCollection AddAppLocalization(this IServiceCollection services)
    {
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.AddSingleton(typeof(Text<>));
        services.AddSingleton<IConfigureOptions<MvcOptions>, LocalizedModelBindingMessages>();
        // Emit non-ASCII letters as-is instead of &#x131;-style entities (HTML-significant characters are still encoded).
        services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
        return services;
    }

    /// <summary>Data annotations (Display names, ErrorMessage) resolve against the resource of the model's area.</summary>
    public static IMvcBuilder AddAppDataAnnotationsLocalization(this IMvcBuilder mvc) =>
        mvc.AddDataAnnotationsLocalization(o =>
            o.DataAnnotationLocalizerProvider = (type, factory) => new CompositeLocalizer(factory, CompositeLocalizer.ResourceFor(type)));

    public static WebApplication UseAppLocalization(this WebApplication app)
    {
        Fmt.Configure(app.Services.GetRequiredService<IStringLocalizerFactory>());
        app.UseRequestLocalization(new RequestLocalizationOptions
        {
            // Culture (parsing/formatting) is invariant on purpose; a cookie asking for "tr" is not a supported
            // *culture*, so the middleware keeps the invariant default while switching the UI culture.
            DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, new CultureInfo(English)),
            SupportedCultures = [CultureInfo.InvariantCulture],
            SupportedUICultures = Languages.Select(l => new CultureInfo(l)).ToList(),
            RequestCultureProviders = [new CookieRequestCultureProvider()]
        });
        return app;
    }

    /// <summary><c>GET /set-language?culture=tr&amp;returnUrl=/App</c>: stores the choice for a year and goes back.</summary>
    public static void MapSetLanguage(this WebApplication app) =>
        app.MapGet("/set-language", (string? culture, string? returnUrl, HttpContext ctx) =>
        {
            var lang = Languages.Contains(culture) ? culture! : English;
            ctx.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(lang)),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
            return Results.Redirect(IsLocal(returnUrl) ? returnUrl! : "/");
        });

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    /// <summary>Localizes MVC's built-in model binding errors ("The value 'x' is not valid for Y.").</summary>
    private sealed class LocalizedModelBindingMessages(IStringLocalizerFactory factory) : IConfigureOptions<MvcOptions>
    {
        public void Configure(MvcOptions o)
        {
            var l = factory.Create(typeof(SharedResource));
            var p = o.ModelBindingMessageProvider;
            p.SetAttemptedValueIsInvalidAccessor((v, f) => l["The value '{0}' is not valid for {1}.", v, f]);
            p.SetMissingBindRequiredValueAccessor(f => l["A value for '{0}' was not provided.", f]);
            p.SetMissingKeyOrValueAccessor(() => l["A value is required."]);
            p.SetMissingRequestBodyRequiredValueAccessor(() => l["A non-empty request body is required."]);
            p.SetNonPropertyAttemptedValueIsInvalidAccessor(v => l["The value '{0}' is not valid.", v]);
            p.SetNonPropertyUnknownValueIsInvalidAccessor(() => l["The supplied value is invalid."]);
            p.SetNonPropertyValueMustBeANumberAccessor(() => l["The field must be a number."]);
            p.SetUnknownValueIsInvalidAccessor(f => l["The supplied value is invalid for {0}.", f]);
            p.SetValueIsInvalidAccessor(v => l["The value '{0}' is not valid.", v]);
            p.SetValueMustBeANumberAccessor(f => l["The field {0} must be a number.", f]);
            p.SetValueMustNotBeNullAccessor(v => l["The value '{0}' is not valid.", v]);
        }
    }
}
