namespace Inventory.Domain;

public static class InventoryPermissions
{
    public const string ProductsView = "inventory.products.view";
    public const string ProductsManage = "inventory.products.manage";
    public const string StockAdjust = "inventory.stock.adjust";
    public const string ReportsView = "inventory.reports.view";

    public static IReadOnlyList<string> All =>
    [
        ProductsView, ProductsManage, StockAdjust, ReportsView
    ];
}

public static class InventoryRoles
{
    public const string Manager = "Inventory Manager";
    public const string StockClerk = "Inventory Stock Clerk";
    public const string Viewer = "Inventory Viewer";
}
