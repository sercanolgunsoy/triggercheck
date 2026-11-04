using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Adds security headers to every response. The CSP allows scripts only from this site (Chart.js is self-hosted) and
/// inline &lt;script&gt; blocks carrying the per-request nonce (stamped automatically by <see cref="CspNonceTagHelper"/>).
/// Inline event handlers are not allowed (data-confirm forms are handled in site.js). Inline styles stay allowed
/// (style="" attributes, Bootstrap JS). HSTS is added separately by UseHsts outside Development.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    public const string NonceItemKey = "TriggerCheck.CspNonce";

    public static string BuildCsp(string nonce) => string.Join("; ",
        "default-src 'self'",
        $"script-src 'self' 'nonce-{nonce}'",
        "style-src 'self' 'unsafe-inline'",
        "img-src 'self' data:",
        "font-src 'self'",
        "connect-src 'self'",
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        "frame-ancestors 'none'");

    public Task InvokeAsync(HttpContext context)
    {
        // Hex: no characters that Razor would HTML-encode in the attribute.
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItemKey] = nonce;
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            // The Development exception page uses inline scripts without our nonce: leave it without a CSP.
            if (!(env.IsDevelopment() && context.Response.StatusCode >= 500))
                h.ContentSecurityPolicy = BuildCsp(nonce);
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            return Task.CompletedTask;
        });
        return next(context);
    }

    /// <summary>The nonce of the current request (null outside the middleware, e.g. in unit-rendered views).</summary>
    public static string? GetNonce(HttpContext context) => context.Items[NonceItemKey] as string;
}

/// <summary>Stamps the request's CSP nonce on every &lt;script&gt; element rendered by Razor.</summary>
[HtmlTargetElement("script")]
public sealed class CspNonceTagHelper : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (SecurityHeadersMiddleware.GetNonce(ViewContext.HttpContext) is { } nonce)
            output.Attributes.SetAttribute("nonce", nonce);
    }
}

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.UseMiddleware<SecurityHeadersMiddleware>();
}
