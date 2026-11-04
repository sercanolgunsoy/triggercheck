using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Monitoring;
using TriggerCheck.Web.Api;
using TriggerCheck.Web.Billing;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMonitoringEngine(builder.Configuration);
builder.Services.AddAppLocalization();
// OPS-4: OpenTelemetry (OTLP export only when configured) and /health/live + /health/ready.
builder.AddAppTelemetry();
builder.Services.AddAppHealthChecks();

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<LocalizedIdentityErrorDescriber>()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AccessDenied";
    o.Cookie.Name = "TriggerCheck.Auth";
    o.SlidingExpiration = true;
    // Role / active-state changes take effect on the next request (security stamp, active state and customer
    // checked per request; replaces the former ActiveAccountFilter).
    SessionValidator.Attach(o);
    // FEAT-8: /api requests are authenticated by the API key scheme only (no cookie, no SessionValidator there).
    o.ForwardDefaultSelector = ApiSetup.ForwardApiRequests;
});
// FEAT-8: REST API (API key scheme + policies, per-key rate limit, OpenAPI document).
builder.Services.AddTriggerCheckApi(builder.Configuration);

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.SuperAdmin, p => p.RequireRole(Roles.SuperAdmin));
    o.AddPolicy(Policies.Customer, p => p.RequireRole(Roles.CustomerAdmin, Roles.CustomerViewer).RequireClaim(AppClaims.CustomerId));
    o.AddPolicy(Policies.CustomerAdmin, p => p.RequireRole(Roles.CustomerAdmin).RequireClaim(AppClaims.CustomerId));
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentCustomer, CurrentCustomer>();
builder.Services.AddScoped<TriggerCheck.Web.Areas.App.Support.MonitorCommands>();
builder.Services.AddSingleton<ManualCheckLimiter>();
// BIZ-1/2/3: self-service sign-up, trial, iyzico subscriptions, usage meters (+ their rate limits and background job).
builder.Services.AddBilling(builder.Configuration);
builder.Services.AddSingleton<TriggerCheck.Web.Areas.App.Pages.Alerts.TestNotificationLimiter>();

builder.Services.AddRazorPages(o =>
{
    // Customer panel: /App/...
    o.Conventions.AuthorizeAreaFolder("App", "/", Policies.Customer);
    // Platform administration: /SuperAdmin/...
    o.Conventions.AuthorizeAreaFolder("SuperAdmin", "/", Policies.SuperAdmin);
}).AddAppDataAnnotationsLocalization();
// Users flagged MustChangePassword (e.g. the seeded SuperAdmin) are sent to /Account/ChangePassword first (all pages, all areas).
builder.Services.Configure<MvcOptions>(o => o.Filters.Add(new MustChangePasswordFilter()));

var app = builder.Build();
app.ValidateBillingConfiguration();

if (!app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Monitoring:AllowPrivateTargets"))
    app.Logger.LogWarning("Monitoring:AllowPrivateTargets is enabled in {Environment}: SSRF protection is OFF and monitors/webhooks " +
                          "can reach internal addresses. Use this only for development or testing.", app.Environment.EnvironmentName);

// Behind a reverse proxy (ForwardedHeaders:Enabled): real client IP and scheme, before anything that uses them.
app.UseAppForwardedHeaders();
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

// English (default) / Turkish UI; parsing stays culture-invariant (see LocalizationSetup).
app.UseAppLocalization();

// FEAT-8, /api only: problem+json for errors and the per-IP limit (before routing and authentication).
app.UseApiPipeline();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after authorization: the API limit is partitioned by the authenticated key

// AllowAnonymous on static assets and the health endpoints also tells SessionValidator to skip its per-request DB check there.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages().WithStaticAssets();
app.MapAppHealthChecks(includeDetails: app.Environment.IsDevelopment());
app.MapSetLanguage();
app.MapBillingEndpoints();
app.MapTriggerCheckApi();

await DbSeeder.SeedAsync(app.Services);

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
