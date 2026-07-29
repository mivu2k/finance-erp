using ErpPlatform.Shared.Identity;
using Hr.Domain;
using Hr.Infrastructure.Attendance;
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
            P(HrPermissions.ReportsView, "Reports", "View headcount and expiry reports"),

            P(HrPermissions.AttendanceViewOwn, "Attendance", "See own attendance"),
            P(HrPermissions.AttendanceViewAll, "Attendance", "See everyone's attendance"),
            P(HrPermissions.AttendanceEdit, "Attendance",
                "Correct attendance by hand when a terminal misses a read"),
            P(HrPermissions.DevicesManage, "Attendance", "Configure attendance stations and enrol cards"),

            P(HrPermissions.LeaveRequest, "Leave", "Apply for leave"),
            P(HrPermissions.LeaveViewOwn, "Leave", "See own leave and balances"),
            P(HrPermissions.LeaveViewAll, "Leave", "See everyone's leave"),
            P(HrPermissions.LeaveApprove, "Leave", "Approve or reject leave requests"),
            P(HrPermissions.LeaveManage, "Leave", "Manage leave types, quotas and balances")
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
                HrPermissions.EmployeesView, HrPermissions.ReportsView,
                HrPermissions.AttendanceViewAll, HrPermissions.LeaveViewAll
            ]),

            // What every member of staff gets: their own record, nobody else's.
            new(HrRoles.SelfService, "Own attendance and leave only.",
            [
                HrPermissions.AttendanceViewOwn,
                HrPermissions.LeaveRequest, HrPermissions.LeaveViewOwn
            ]),

            new(HrRoles.LineManager, "Approves the team's leave and sees their attendance.",
            [
                HrPermissions.EmployeesView,
                HrPermissions.AttendanceViewOwn, HrPermissions.AttendanceViewAll,
                HrPermissions.LeaveRequest, HrPermissions.LeaveViewOwn,
                HrPermissions.LeaveViewAll, HrPermissions.LeaveApprove
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
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IAttendanceSyncService, AttendanceSyncService>();
        services.AddScoped<ILeaveService, LeaveService>();
        services.AddScoped<IKioskService, KioskService>();
        services.AddSingleton<IAttendanceTokenService, AttendanceTokenService>();
        services.AddSingleton<IHrExportService, HrExportService>();

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

        if (!await db.Shifts.AnyAsync())
        {
            db.Shifts.Add(new Shift
            {
                Name = "General",
                StartsAt = new TimeOnly(9, 0),
                EndsAt = new TimeOnly(17, 0),
                GraceMinutes = 15,
                HalfDayMinutes = 240,
                MinimumMinutes = 60,
                OvertimeAfterMinutes = 30,
                WeeklyOffMask = 1 << (int)DayOfWeek.Sunday,
                IsDefault = true
            });
            logger.LogInformation("Seeded the default shift");
        }

        if (!await db.LeaveTypes.AnyAsync())
        {
            db.LeaveTypes.AddRange(
                new LeaveType { Name = "Annual Leave", Code = "AL", AnnualQuota = 14, IsPaid = true,
                    AllowCarryForward = true, MaxCarryForward = 7, Colour = "#1976d2" },
                new LeaveType { Name = "Sick Leave", Code = "SL", AnnualQuota = 8, IsPaid = true,
                    DocumentRequiredAfterDays = 2, Colour = "#d32f2f" },
                new LeaveType { Name = "Casual Leave", Code = "CL", AnnualQuota = 10, IsPaid = true,
                    Colour = "#f57c00" },
                new LeaveType { Name = "Unpaid Leave", Code = "UL", AnnualQuota = 0, IsPaid = false,
                    Colour = "#616161" },
                new LeaveType { Name = "Maternity Leave", Code = "ML", AnnualQuota = 90, IsPaid = true,
                    Colour = "#7b1fa2" },
                new LeaveType { Name = "Bereavement", Code = "BL", AnnualQuota = 3, IsPaid = true,
                    Colour = "#455a64" });
            logger.LogInformation("Seeded leave types");
        }

        await db.SaveChangesAsync();
    }
}
