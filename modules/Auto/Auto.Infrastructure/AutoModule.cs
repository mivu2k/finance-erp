using Auto.Domain;
using ErpPlatform.Shared.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auto.Infrastructure;

public static class AutoModule
{
    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.Auto, group, description);

    public static ModuleRegistration Registration => new(
        AppModules.Auto,
        [
            P(AutoPermissions.VehiclesView, "Vehicles", "View the vehicle fleet"),
            P(AutoPermissions.VehiclesManage, "Vehicles", "Add and edit vehicles"),
            P(AutoPermissions.MaintenanceRecord, "Maintenance", "Log maintenance against a vehicle"),
            P(AutoPermissions.ReportsView, "Reports", "Cost and upcoming-maintenance reports")
        ],
        [
            new(AutoRoles.Manager, "Full control of the fleet.", AutoPermissions.All),

            new(AutoRoles.Mechanic, "Logs maintenance but doesn't manage the fleet roster.",
            [
                AutoPermissions.VehiclesView, AutoPermissions.MaintenanceRecord,
                AutoPermissions.ReportsView
            ]),

            new(AutoRoles.Viewer, "Read-only.",
            [
                AutoPermissions.VehiclesView, AutoPermissions.ReportsView
            ])
        ]);

    public static IServiceCollection AddAutoModule(
        this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("AutoConnection")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:AutoConnection is not configured.");

        services.AddDbContext<AutoDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs), my => my.EnableRetryOnFailure(3)));

        services.AddScoped<IVehicleService, VehicleService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    public static async Task SeedAsync(AutoDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Auto database is up to date");
    }
}
