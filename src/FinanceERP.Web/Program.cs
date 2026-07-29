using ErpPlatform.Shared.Identity;
using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Web.Security;
using FinanceERP.Infrastructure;
using GatePass.Infrastructure;
using Hr.Infrastructure;
using Repair.Infrastructure;
using FinanceERP.Infrastructure.Persistence;
using FinanceERP.Web.Components;
using FinanceERP.Web.Components.Account;
using FinanceERP.Web.Endpoints;
using GatePass.Web;
using Hr.Web;
using Repair.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/finance-erp-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

// Persist data-protection keys to a stable folder so login cookies survive
// restarts and redeploys (also silences the "No XML encryptor" warning path).
// A single application name across every host in the platform, so one login cookie
// is valid in all of them if the apps are ever split into separate processes.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")))
    .SetApplicationName("ErpPlatform");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Shared identity database: users, roles, permission claims and app access.
builder.Services.AddPlatformIdentity(builder.Configuration);

// Business modules, each on its own database. Adding a module here is what puts
// its tile on the portal and its roles into the identity seeder.
builder.Services.AddFinanceModule(builder.Configuration);
builder.Services.AddHrModule(builder.Configuration);
builder.Services.AddGatePassModule(builder.Configuration);
builder.Services.AddRepairModule(builder.Configuration);

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// Dynamic permission-based authorization (permissions live in AspNetRoleClaims).
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, ModuleAccessHandler>();

builder.Services.AddHostedService<FinanceERP.Web.Services.AlertsBackgroundService>();
builder.Services.AddSingleton<FinanceERP.Web.Services.ReceiptStorage>();

var app = builder.Build();

// Migrate and seed each database in turn. Identity goes first: it creates the
// roles and the admin account that the module seeders then mirror against.
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var logger = sp.GetRequiredService<ILogger<Program>>();

    await IdentitySeeder.SeedAsync(
        sp.GetRequiredService<PlatformIdentityDbContext>(),
        sp.GetRequiredService<UserManager<ApplicationUser>>(),
        sp.GetRequiredService<RoleManager<ApplicationRole>>(),
        sp.GetRequiredService<IConfiguration>(),
        logger);

    await DbSeeder.SeedAsync(
        sp.GetRequiredService<AppDbContext>(),
        sp.GetRequiredService<IPlatformUserDirectory>(),
        logger);

    // One-time backfill: the letterhead used to be a Finance-only "Company.Name"
    // setting. Carry it into the platform profile so an upgraded install doesn't
    // start printing blank headers.
    await SeedCompanyProfileAsync(sp, logger);

    await HrModule.SeedAsync(sp.GetRequiredService<HrDbContext>(), logger);
    await GatePassModule.SeedAsync(sp.GetRequiredService<GatePassDbContext>(), logger);
    await RepairModule.SeedAsync(sp.GetRequiredService<RepairDbContext>(), logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // The portal and app switcher live in the shared web library; endpoint routing
    // needs them listed here as well as on the Router.
    .AddAdditionalAssemblies(
        typeof(ErpPlatform.Shared.Web.Portal.Portal).Assembly,
        typeof(Hr.Web.Layout.HrLayout).Assembly,
        typeof(GatePass.Web.Layout.GatePassLayout).Assembly,
        typeof(Repair.Web.Layout.RepairLayout).Assembly);

app.MapAdditionalIdentityEndpoints();
app.MapExportEndpoints();
app.MapPrintEndpoints();
app.MapGatePassPrintEndpoints();
app.MapRepairPrintEndpoints();
app.MapHrExportEndpoints();

// Authenticated receipt downloads (files live outside wwwroot).
app.MapGet("/files/receipts/{name}", (string name, FinanceERP.Web.Services.ReceiptStorage storage) =>
{
    var path = storage.Resolve(name);
    return path is null
        ? Results.NotFound()
        : Results.File(path, FinanceERP.Web.Services.ReceiptStorage.ContentType(path));
}).RequireAuthorization();

app.Run();

static async Task SeedCompanyProfileAsync(IServiceProvider sp, Microsoft.Extensions.Logging.ILogger logger)
{
    var identity = sp.GetRequiredService<PlatformIdentityDbContext>();
    if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .AnyAsync(identity.CompanyProfiles)) return;

    var legacy = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .FirstOrDefaultAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(
                sp.GetRequiredService<AppDbContext>().AppSettings),
            s => s.Key == FinanceERP.Domain.Entities.SettingKeys.CompanyName))?.Value;

    identity.CompanyProfiles.Add(new CompanyProfile
    {
        Name = string.IsNullOrWhiteSpace(legacy) ? "" : legacy,
        ModifiedAtUtc = DateTime.UtcNow,
        ModifiedBy = "seed"
    });
    await identity.SaveChangesAsync();
    logger.LogInformation("Seeded the company profile (name: {Name}).",
        string.IsNullOrWhiteSpace(legacy) ? "not set — configure at /admin/company" : legacy);
}
