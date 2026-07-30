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
            P(InventoryPermissions.ReportsView, "Reports", "Stock levels and low-stock reports"),
            P(InventoryPermissions.CostsView, "Reports", "See cost, sale price and stock valuation"),
            P(InventoryPermissions.CountManage, "Stock", "Run a stock take and post its variances")
        ],
        [
            new(InventoryRoles.Manager, "Full control of products and stock.", InventoryPermissions.All),

            // Counts and moves stock, but money stays hidden: a storeman needs to know
            // what is on the shelf, not what it is worth.
            new(InventoryRoles.StockClerk, "Adjusts stock and counts it, without seeing costs.",
            [
                InventoryPermissions.ProductsView, InventoryPermissions.StockAdjust,
                InventoryPermissions.ReportsView, InventoryPermissions.CountManage
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
        services.AddScoped<IStockTrackingService, StockTrackingService>();
        services.AddScoped<IStockCountService, StockCountService>();
        services.AddScoped<IWarehouseService, WarehouseService>();
        services.AddScoped<IStockTransferService, StockTransferService>();

        ModuleRegistry.Register(Registration);
        return services;
    }

    public static async Task SeedAsync(InventoryDbContext db, ILogger logger)
    {
        await db.Database.MigrateAsync();

        // Everything that existed before warehouses needs somewhere to belong, and
        // an unnamed movement needs a default to land in.
        if (!await db.Warehouses.AnyAsync())
        {
            db.Warehouses.Add(new Warehouse
            {
                Name = "Main Store",
                Code = "MAIN",
                IsDefault = true,
                Notes = "Created automatically — rename it to match your actual store."
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded the default inventory warehouse");
        }

        logger.LogInformation("Inventory database is up to date");
    }
}
