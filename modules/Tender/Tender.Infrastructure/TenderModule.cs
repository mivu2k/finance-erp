using ErpPlatform.Shared.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tender.Domain;

namespace Tender.Infrastructure;

public static class TenderModule
{
    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.Tender, group, description);

    public static ModuleRegistration Registration => new(
        AppModules.Tender,
        [
            P(TenderPermissions.TendersView, "Tenders", "View tender and project records"),
            P(TenderPermissions.TendersManage, "Tenders", "Add and edit tender records"),
            P(TenderPermissions.GuaranteesManage, "Guarantees", "Record EMDs, bank guarantees and other securities"),
            P(TenderPermissions.DocumentsManage, "Documents", "Log tender-related documents"),
            P(TenderPermissions.ReportsView, "Reports", "Pipeline, win-rate and guarantee-expiry reports")
        ],
        [
            new(TenderRoles.Manager, "Full control of tenders, guarantees and documents.", TenderPermissions.All),

            new(TenderRoles.Officer, "Prepares and tracks tenders day to day.",
            [
                TenderPermissions.TendersView, TenderPermissions.TendersManage,
                TenderPermissions.GuaranteesManage, TenderPermissions.DocumentsManage,
                TenderPermissions.ReportsView
            ]),

            new(TenderRoles.Viewer, "Read-only.",
            [
                TenderPermissions.TendersView, TenderPermissions.ReportsView
            ])
        ]);

    public static IServiceCollection AddTenderModule(
        this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("TenderConnection")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:TenderConnection is not configured.");

        services.AddDbContext<TenderDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs), my => my.EnableRetryOnFailure(3)));

        services.AddScoped<ITenderService, TenderService>();
        services.AddScoped<ITenderReportService, TenderReportService>();
        services.AddScoped<ITenderPrintService, TenderPrintService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    public static async Task SeedAsync(TenderDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Tender database is up to date");
    }
}
