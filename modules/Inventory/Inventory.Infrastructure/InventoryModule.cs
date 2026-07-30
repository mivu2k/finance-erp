using ErpPlatform.Shared.Identity;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure;

public static class InventoryModule
{
    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.Inventory, group, description);

    public static ModuleRegistration Registration => new(
        AppModules.Inventory,
        [
            P(InventoryPermissions.ProductsView, "Products", "View products, models and accessories"),
            P(InventoryPermissions.ProductsManage, "Products", "Create and edit products, models and accessories"),
            P(InventoryPermissions.StockAdjust, "Stock", "Adjust stock quantities in or out"),
            P(InventoryPermissions.ReportsView, "Reports", "Stock levels and low-stock reports")
        ],
        [
            new(InventoryRoles.Manager, "Full control of products and stock.", InventoryPermissions.All),

            new(InventoryRoles.StockClerk, "Adjusts stock but doesn't edit the product catalog.",
            [
                InventoryPermissions.ProductsView, InventoryPermissions.StockAdjust,
                InventoryPermissions.ReportsView
            ]),

            new(InventoryRoles.Viewer, "Read-only.",
            [
                InventoryPermissions.ProductsView, InventoryPermissions.ReportsView
            ])
        ]);

    public static IServiceCollection AddInventoryModule(
        this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("InventoryConnection")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:InventoryConnection is not configured.");

        services.AddDbContext<InventoryDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs), my => my.EnableRetryOnFailure(3)));

        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IStockService, StockService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    public static async Task SeedAsync(InventoryDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Inventory database is up to date");
    }
}
