using ErpPlatform.Shared.Identity;
using Hr.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hr.Infrastructure;

public static class HrModule
{
    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.Hr, group, description);

    public static ModuleRegistration Registration => new(
        AppModules.Hr,
        [
            P(HrPermissions.EmployeesView, "Employees", "View the employee directory"),
            P(HrPermissions.EmployeesManage, "Employees", "Create, edit and separate employees"),
            P(HrPermissions.EmployeesViewSensitive, "Employees",
                "See bank, tax and national ID details"),
            P(HrPermissions.DocumentsView, "Documents", "View employee documents"),
            P(HrPermissions.DocumentsManage, "Documents", "Upload and remove employee documents"),
            P(HrPermissions.CatalogManage, "Setup", "Manage departments and designations"),
            P(HrPermissions.ReportsView, "Reports", "View headcount and expiry reports")
        ],
        [
            new(HrRoles.Manager, "Full control of employee records and HR setup.",
                HrPermissions.All),
            new(HrRoles.Officer, "Day-to-day record keeping, without sensitive financial detail.",
            [
                HrPermissions.EmployeesView, HrPermissions.EmployeesManage,
                HrPermissions.DocumentsView, HrPermissions.DocumentsManage,
                HrPermissions.ReportsView
            ]),
            new(HrRoles.Viewer, "Read-only access to the employee directory.",
            [
                HrPermissions.EmployeesView, HrPermissions.ReportsView
            ])
        ]);

    /// <summary>
    /// Registers HR: its own database, its services and its role catalog.
    /// Requires <c>AddPlatformIdentity</c> to have run first.
    /// </summary>
    public static IServiceCollection AddHrModule(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("HrConnection")
                 ?? throw new InvalidOperationException("ConnectionStrings:HrConnection is not configured.");

        services.AddDbContext<HrDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs), my => my.EnableRetryOnFailure(3)));

        services.AddScoped<IEmployeeService, EmployeeService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    /// <summary>Migrates the HR database and seeds a starter catalog.</summary>
    public static async Task SeedAsync(HrDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();

        if (!await db.Departments.AnyAsync())
        {
            db.Departments.AddRange(
                new Department { Name = "Administration", Code = "ADM" },
                new Department { Name = "Finance", Code = "FIN" },
                new Department { Name = "Operations", Code = "OPS" },
                new Department { Name = "Workshop", Code = "WRK" },
                new Department { Name = "Sales", Code = "SAL" },
                new Department { Name = "IT", Code = "IT" });
            logger.LogInformation("Seeded HR departments");
        }

        if (!await db.Designations.AnyAsync())
        {
            db.Designations.AddRange(
                new Designation { Title = "Manager" },
                new Designation { Title = "Supervisor" },
                new Designation { Title = "Officer" },
                new Designation { Title = "Technician" },
                new Designation { Title = "Accountant" },
                new Designation { Title = "Driver" },
                new Designation { Title = "Helper" });
            logger.LogInformation("Seeded HR designations");
        }

        await db.SaveChangesAsync();
    }
}
