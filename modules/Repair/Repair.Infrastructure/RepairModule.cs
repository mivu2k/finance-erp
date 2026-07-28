using ErpPlatform.Shared.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Repair.Domain;
using Repair.Infrastructure.Reports;

namespace Repair.Infrastructure;

public static class RepairModule
{
    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.Repair, group, description);

    public static ModuleRegistration Registration => new(
        AppModules.Repair,
        [
            P(RepairPermissions.CustomersView, "Customers", "View customers"),
            P(RepairPermissions.CustomersManage, "Customers", "Create and edit customers"),

            P(RepairPermissions.IntakesView, "Intakes", "View intakes"),
            P(RepairPermissions.IntakesManage, "Intakes", "Book devices in at the counter"),

            P(RepairPermissions.JobsView, "Repair Jobs", "View repair jobs"),
            P(RepairPermissions.JobsManage, "Repair Jobs", "Edit job details and move status"),
            P(RepairPermissions.JobsAssign, "Repair Jobs", "Assign jobs to technicians"),
            P(RepairPermissions.JobsDiagnose, "Repair Jobs", "Record diagnoses and work done"),
            P(RepairPermissions.JobsDeliver, "Repair Jobs", "Hand a device back to the customer"),

            P(RepairPermissions.PartsView, "Parts & Purchasing", "View the parts catalog and costs"),
            P(RepairPermissions.PartsManage, "Parts & Purchasing", "Manage parts and prices"),
            P(RepairPermissions.PurchasesView, "Parts & Purchasing",
                "View purchases and supplier spend"),
            P(RepairPermissions.PurchasesManage, "Parts & Purchasing",
                "Record purchases and manage suppliers"),

            P(RepairPermissions.QuotationsView, "Quotations", "View quotations"),
            P(RepairPermissions.QuotationsManage, "Quotations", "Prepare and send quotations"),
            P(RepairPermissions.QuotationsApprove, "Quotations",
                "Record manager and customer approval"),

            P(RepairPermissions.OrdersView, "Sales Orders", "View sales orders and invoices"),
            P(RepairPermissions.OrdersManage, "Sales Orders", "Turn quotations into orders"),
            P(RepairPermissions.PaymentsRecord, "Sales Orders", "Record customer payments"),

            P(RepairPermissions.ReportsView, "Reports", "Workshop tracking and reports"),
            P(RepairPermissions.ReportsFinancial, "Reports",
                "Reports showing revenue, margin, receivables and supplier cost"),
            P(RepairPermissions.CatalogManage, "Setup",
                "Manage symptoms, accessories, brands and device types")
        ],
        [
            new(RepairRoles.Manager, "Full control of the repair operation.",
                RepairPermissions.All),

            new(RepairRoles.Supervisor, "Runs the workshop floor and approves quotations.",
            [
                RepairPermissions.CustomersView, RepairPermissions.IntakesView,
                RepairPermissions.IntakesManage, RepairPermissions.JobsView,
                RepairPermissions.JobsManage, RepairPermissions.JobsAssign,
                RepairPermissions.JobsDiagnose, RepairPermissions.JobsDeliver,
                RepairPermissions.PartsView, RepairPermissions.QuotationsView,
                RepairPermissions.QuotationsManage, RepairPermissions.QuotationsApprove,
                RepairPermissions.OrdersView, RepairPermissions.ReportsView,
                RepairPermissions.ReportsFinancial, RepairPermissions.PurchasesView
            ]),

            new(RepairRoles.Technician, "Works the bench: sees assigned jobs and records findings.",
            [
                RepairPermissions.JobsView, RepairPermissions.JobsDiagnose,
                RepairPermissions.JobsManage, RepairPermissions.PartsView,
                RepairPermissions.CustomersView
            ]),

            new(RepairRoles.Sales, "Front counter: books devices in and deals with customers.",
            [
                RepairPermissions.CustomersView, RepairPermissions.CustomersManage,
                RepairPermissions.IntakesView, RepairPermissions.IntakesManage,
                RepairPermissions.JobsView, RepairPermissions.QuotationsView,
                RepairPermissions.QuotationsManage, RepairPermissions.OrdersView
            ]),

            new(RepairRoles.Store, "Looks after parts and stock.",
            [
                RepairPermissions.PartsView, RepairPermissions.PartsManage,
                RepairPermissions.PurchasesView, RepairPermissions.PurchasesManage,
                RepairPermissions.JobsView, RepairPermissions.ReportsView
            ]),

            new(RepairRoles.Accountant, "Bills the work and takes the money.",
            [
                RepairPermissions.CustomersView, RepairPermissions.QuotationsView,
                RepairPermissions.OrdersView, RepairPermissions.OrdersManage,
                RepairPermissions.PaymentsRecord, RepairPermissions.ReportsView,
                RepairPermissions.ReportsFinancial, RepairPermissions.PurchasesView,
                RepairPermissions.JobsView
            ]),

            new(RepairRoles.Viewer, "Read-only.",
            [
                RepairPermissions.CustomersView, RepairPermissions.IntakesView,
                RepairPermissions.JobsView, RepairPermissions.QuotationsView,
                RepairPermissions.OrdersView, RepairPermissions.PartsView,
                RepairPermissions.ReportsView
            ])
        ]);

    public static IServiceCollection AddRepairModule(
        this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("RepairConnection")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:RepairConnection is not configured.");

        services.AddDbContext<RepairDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs), my => my.EnableRetryOnFailure(3)));

        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IIntakeService, IntakeService>();
        services.AddScoped<IRepairJobService, RepairJobService>();
        services.AddScoped<IQuotationService, QuotationService>();
        services.AddScoped<ISalesOrderService, SalesOrderService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IPurchaseService, PurchaseService>();
        services.AddScoped<IRepairReportService, RepairReportService>();
        services.AddScoped<ReportTableBuilder>();
        services.AddSingleton<IReportExportService, ReportExportService>();
        services.AddSingleton<IRepairPrintService, RepairPrintService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    /// <summary>Migrates the database and seeds the dropdown catalogs.</summary>
    public static async Task SeedAsync(RepairDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();

        if (!await db.Symptoms.AnyAsync())
        {
            db.Symptoms.AddRange(
                new Symptom { Name = "Won't power on", Category = "Power" },
                new Symptom { Name = "Intermittent power", Category = "Power" },
                new Symptom { Name = "Overheating", Category = "Thermal" },
                new Symptom { Name = "Unusual noise", Category = "Mechanical" },
                new Symptom { Name = "Vibration", Category = "Mechanical" },
                new Symptom { Name = "Oil / fluid leak", Category = "Mechanical" },
                new Symptom { Name = "Display fault", Category = "Electronics" },
                new Symptom { Name = "Control panel unresponsive", Category = "Electronics" },
                new Symptom { Name = "Error code shown", Category = "Electronics" },
                new Symptom { Name = "Physical damage", Category = "Body" });
            logger.LogInformation("Seeded repair symptoms");
        }

        if (!await db.Accessories.AnyAsync())
        {
            db.Accessories.AddRange(
                new Accessory { Name = "Power cable" },
                new Accessory { Name = "Battery" },
                new Accessory { Name = "Charger" },
                new Accessory { Name = "Carry case" },
                new Accessory { Name = "Remote control" },
                new Accessory { Name = "Manual" },
                new Accessory { Name = "Tool kit" });
            logger.LogInformation("Seeded repair accessories");
        }

        if (!await db.DeviceTypes.AnyAsync())
        {
            db.DeviceTypes.AddRange(
                new DeviceType { Name = "Generator" },
                new DeviceType { Name = "Compressor" },
                new DeviceType { Name = "Pump" },
                new DeviceType { Name = "Welding machine" },
                new DeviceType { Name = "Power tool" },
                new DeviceType { Name = "UPS" },
                new DeviceType { Name = "Other" });
            logger.LogInformation("Seeded repair device types");
        }

        await db.SaveChangesAsync();
    }
}
