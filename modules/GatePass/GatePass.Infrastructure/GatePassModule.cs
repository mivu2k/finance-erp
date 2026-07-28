using ErpPlatform.Shared.Identity;
using GatePass.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GatePass.Infrastructure;

public static class GatePassModule
{
    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.GatePass, group, description);

    public static ModuleRegistration Registration => new(
        AppModules.GatePass,
        [
            P(GatePassPermissions.PassesView, "Gate Passes", "View gate passes"),
            P(GatePassPermissions.PassesCreate, "Gate Passes", "Raise a gate pass"),
            P(GatePassPermissions.PassesAuthorize, "Gate Passes", "Authorise and edit gate passes"),
            P(GatePassPermissions.PassesComplete, "Gate Passes",
                "Pass goods through the gate and record returns"),
            P(GatePassPermissions.PassesCancel, "Gate Passes", "Cancel a gate pass"),

            P(GatePassPermissions.DemosView, "Demo Goods", "View demo issuances"),
            P(GatePassPermissions.DemosManage, "Demo Goods", "Issue and edit demo goods"),
            P(GatePassPermissions.DemosReturn, "Demo Goods", "Record demo goods coming back"),

            P(GatePassPermissions.ReportsView, "Reports", "Outstanding and overdue reports")
        ],
        [
            new(GatePassRoles.Manager, "Full control of gate passes and demo goods.",
                GatePassPermissions.All),

            new(GatePassRoles.Issuer, "Raises and authorises passes and issues demo goods.",
            [
                GatePassPermissions.PassesView, GatePassPermissions.PassesCreate,
                GatePassPermissions.PassesAuthorize, GatePassPermissions.PassesCancel,
                GatePassPermissions.DemosView, GatePassPermissions.DemosManage,
                GatePassPermissions.ReportsView
            ]),

            // The gate hut: sees passes and marks goods through, but can't raise or
            // edit one — that separation is the point of a gate pass.
            new(GatePassRoles.Security, "Checks passes at the gate and records movements.",
            [
                GatePassPermissions.PassesView, GatePassPermissions.PassesComplete,
                GatePassPermissions.DemosView, GatePassPermissions.DemosReturn
            ]),

            new(GatePassRoles.Viewer, "Read-only.",
            [
                GatePassPermissions.PassesView, GatePassPermissions.DemosView,
                GatePassPermissions.ReportsView
            ])
        ]);

    public static IServiceCollection AddGatePassModule(
        this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("GatePassConnection")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:GatePassConnection is not configured.");

        services.AddDbContext<GatePassDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs), my => my.EnableRetryOnFailure(3)));

        services.AddScoped<IGatePassService, GatePassService>();
        services.AddScoped<IDemoIssuanceService, DemoIssuanceService>();
        services.AddSingleton<IGatePassPrintService, GatePassPrintService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    public static async Task SeedAsync(GatePassDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Gate Pass database is up to date");
    }
}
