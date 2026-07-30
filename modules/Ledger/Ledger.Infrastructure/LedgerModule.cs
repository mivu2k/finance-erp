using ErpPlatform.Shared.Identity;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure;

public static class LedgerModule
{
    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.Ledger, group, description);

    public static ModuleRegistration Registration => new(
        AppModules.Ledger,
        [
            P(LedgerPermissions.View, "Ledgers", "View ledgers and statements"),
            P(LedgerPermissions.Manage, "Ledgers", "Open, edit and close ledgers"),
            P(LedgerPermissions.EntryRecord, "Entries", "Record entries and transfers"),
            P(LedgerPermissions.EntryAmend, "Entries", "Amend or remove an entry already written"),
            P(LedgerPermissions.ReportsView, "Reports", "Outstanding balances, tree and head reports"),
            P(LedgerPermissions.HeadsManage, "Heads", "Maintain the ledger heads")
        ],
        [
            new(LedgerRoles.Manager, "Full control, including the heads.",
                LedgerPermissions.All),

            // Writes the day's entries but can't rewrite history or change the heads.
            new(LedgerRoles.Clerk, "Records entries and transfers.",
            [
                LedgerPermissions.View, LedgerPermissions.EntryRecord,
                LedgerPermissions.ReportsView
            ]),

            new(LedgerRoles.Viewer, "Read-only.",
            [
                LedgerPermissions.View, LedgerPermissions.ReportsView
            ])
        ]);

    public static IServiceCollection AddLedgerModule(
        this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("LedgerConnection")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:LedgerConnection is not configured.");

        services.AddDbContext<LedgerDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs), my => my.EnableRetryOnFailure(3)));

        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<ILedgerHeadService, LedgerHeadService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    public static async Task SeedAsync(LedgerDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Ledger database is up to date");
    }
}
