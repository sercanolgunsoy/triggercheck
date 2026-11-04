using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Monitoring;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMonitoringEngine(builder.Configuration);
builder.Services.AddAppLocalization();

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
    // Role / active-state changes take effect on the next request (security stamp checked per request).
    SessionValidator.Attach(o);
});

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.SuperAdmin, p => p.RequireRole(Roles.SuperAdmin));
    o.AddPolicy(Policies.Customer, p => p.RequireRole(Roles.CustomerAdmin, Roles.CustomerViewer).RequireClaim(AppClaims.CustomerId));
    o.AddPolicy(Policies.CustomerAdmin, p => p.RequireRole(Roles.CustomerAdmin).RequireClaim(AppClaims.CustomerId));
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentCustomer, CurrentCustomer>();
builder.Services.AddScoped<ActiveAccountFilter>();

builder.Services.AddRazorPages(o =>
{
    // Customer panel: /App/...
    o.Conventions.AuthorizeAreaFolder("App", "/", Policies.Customer);
    o.Conventions.AddAreaFolderApplicationModelConvention("App", "/", m => m.Filters.Add(new ServiceFilterAttribute(typeof(ActiveAccountFilter))));
    // Platform administration: /SuperAdmin/...
    o.Conventions.AuthorizeAreaFolder("SuperAdmin", "/", Policies.SuperAdmin);
}).AddAppDataAnnotationsLocalization();
// Users flagged MustChangePassword (e.g. the seeded SuperAdmin) are sent to /Account/ChangePassword first (all pages, all areas).
builder.Services.Configure<MvcOptions>(o => o.Filters.Add(new MustChangePasswordFilter()));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

// English (default) / Turkish UI; parsing stays culture-invariant (see LocalizationSetup).
app.UseAppLocalization();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapSetLanguage();

await DbSeeder.SeedAsync(app.Services);

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
