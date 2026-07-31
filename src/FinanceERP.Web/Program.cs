using MudBlazor;
using Hr.Domain;
using ErpPlatform.Shared.Web.Layout;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using ErpPlatform.Shared.Identity;
using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Web.Security;
using FinanceERP.Infrastructure;
using GatePass.Infrastructure;
using Hr.Infrastructure;
using Repair.Infrastructure;
using Inventory.Infrastructure;
using Auto.Infrastructure;
using Ledger.Infrastructure;
using Tender.Infrastructure;
using FinanceERP.Infrastructure.Persistence;
using FinanceERP.Web.Components;
using FinanceERP.Web.Components.Account;
using FinanceERP.Web.Endpoints;
using GatePass.Web;
using Hr.Web;
using Repair.Web;
using Inventory.Web;
using Auto.Web;
using Ledger.Web;
using Tender.Web;
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

// Health checks, so a deploy can be verified for real rather than by curling / for a
// 302. /health/live says the process is up; /health/ready says every database it needs
// is actually reachable, which is the failure the update script most needs to catch.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PlatformIdentityDbContext>("identity", tags: ["ready"])
    .AddDbContextCheck<AppDbContext>("finance", tags: ["ready"]);

// One clock for the whole platform. "Today" is a business fact and must be read in the
// business's timezone; timestamps stay UTC. Configure with Platform:TimeZone.
builder.Services.AddSingleton<IBusinessClock>(
    _ => new BusinessClock(builder.Configuration["Platform:TimeZone"]));
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Shared identity database: users, roles, permission claims and app access.
builder.Services.AddPlatformIdentity(builder.Configuration);

// Business modules, each on its own database. Adding a module here is what puts
// its tile on the portal and its roles into the identity seeder.
builder.Services.AddFinanceModule(builder.Configuration);
builder.Services.AddHrModule(builder.Configuration);

// The rotating attendance QR belongs one tap away, not four clicks deep inside HR:
// people open it at a door, twice a day, with a queue behind them. Registered here in
// the composition root so the shared shell needs no reference to the HR module.
ShellQuickActions.Register(new ShellQuickAction(
    Key: "hr.my-attendance-code",
    Title: "My attendance code",
    Icon: Icons.Material.Filled.QrCode2,
    Href: "/hr/my-code",
    Policy: HrPermissions.AttendanceViewOwn,
    Order: 10));
builder.Services.AddGatePassModule(builder.Configuration);
builder.Services.AddRepairModule(builder.Configuration);
builder.Services.AddInventoryModule(builder.Configuration);
builder.Services.AddAutoModule(builder.Configuration);
builder.Services.AddLedgerModule(builder.Configuration);
builder.Services.AddTenderModule(builder.Configuration);

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
    await InventoryModule.SeedAsync(sp.GetRequiredService<InventoryDbContext>(), logger);
    await AutoModule.SeedAsync(sp.GetRequiredService<AutoDbContext>(), logger);
    await LedgerModule.SeedAsync(sp.GetRequiredService<LedgerDbContext>(), logger);
    await TenderModule.SeedAsync(sp.GetRequiredService<TenderDbContext>(), logger);
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
        typeof(Repair.Web.Layout.RepairLayout).Assembly,
        typeof(Inventory.Web.Layout.InventoryLayout).Assembly,
        typeof(Auto.Web.Layout.AutoLayout).Assembly,
        typeof(Ledger.Web.Layout.LedgerLayout).Assembly,
        typeof(Tender.Web.Layout.TenderLayout).Assembly);

app.MapAdditionalIdentityEndpoints();
app.MapExportEndpoints();
app.MapPrintEndpoints();
app.MapGatePassPrintEndpoints();
app.MapRepairPrintEndpoints();
app.MapHrExportEndpoints();
app.MapHrKioskEndpoints();
app.MapInventoryPrintEndpoints();
app.MapTenderPrintEndpoints();

// Receipt downloads. Files live outside wwwroot, so this endpoint is the only way to
// reach them — which makes it the only place the ownership rule can be enforced.
//
// Being merely authenticated is not enough: a receipt is a scan of somebody's expense
// and often carries a name, an amount and sometimes a bank detail. Until now any signed-in
// user who knew a filename could fetch any receipt, and GUID filenames are obscurity
// rather than a control. You may read a receipt when you can see the document it hangs
// off — a payment request you raised, or one of those you are entitled to see anyway.
// Liveness never touches a database: a slow query must not make the orchestrator
// think the process is dead and restart it mid-request.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.MapGet("/files/receipts/{name}", async (
    string name,
    FinanceERP.Web.Services.ReceiptStorage storage,
    FinanceERP.Infrastructure.Persistence.AppDbContext db,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    var path = storage.Resolve(name);
    if (path is null) return Results.NotFound();

    // Whoever can see every request, or post to the ledger, can see the paperwork
    // behind it — that is the same population that can already open the record itself.
    var seesEverything =
        user.HasClaim(PermissionCatalog.ClaimType, FinanceERP.Domain.Security.Permissions.RequestsViewAll)
        || user.HasClaim(PermissionCatalog.ClaimType, FinanceERP.Domain.Security.Permissions.VouchersView);

    if (!seesEverything)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Forbid();

        var ownsIt = await db.PaymentRequests
            .AsNoTracking()
            .AnyAsync(r => r.RequesterId == userId
                           && r.Lines.Any(l => l.AttachmentPath == name), ct);

        // 404, not 403: a 403 confirms the file exists, which is itself a disclosure.
        if (!ownsIt) return Results.NotFound();
    }

    return Results.File(path, FinanceERP.Web.Services.ReceiptStorage.ContentType(path));
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
