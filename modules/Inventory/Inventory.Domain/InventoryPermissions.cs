namespace Inventory.Domain;

public static class InventoryPermissions
{
    public const string ProductsView = "inventory.products.view";
    public const string ProductsManage = "inventory.products.manage";
    public const string StockAdjust = "inventory.stock.adjust";
    public const string ReportsView = "inventory.reports.view";
    /// <summary>
    /// Seeing cost, sale price and stock valuation. Separate from viewing stock so a
    /// storeman can count what is on the shelf without seeing what it is worth.
    /// </summary>
    public const string CostsView = "inventory.costs.view";
    /// <summary>Running a stock take and posting its variances.</summary>
    public const string CountManage = "inventory.count.manage";
    /// <summary>Maintaining warehouses and transferring stock between them.</summary>
    public const string WarehouseManage = "inventory.warehouses.manage";
    /// <summary>Suppliers, purchase orders and goods received notes.</summary>
    public const string PurchaseManage = "inventory.purchasing.manage";
    /// <summary>Customers, sales orders and delivery notes.</summary>
    public const string SalesManage = "inventory.sales.manage";
    /// <summary>
    /// Issuing a delivery out of stock. Separate from writing the paperwork so the
    /// person who takes the order isn't necessarily the one who moves the goods.
    /// </summary>
    public const string DeliveryPost = "inventory.delivery.post";

    public static IReadOnlyList<string> All =>
    [
        ProductsView, ProductsManage, StockAdjust, ReportsView, CostsView, CountManage,
        WarehouseManage, PurchaseManage, SalesManage, DeliveryPost
    ];
}

public static class InventoryRoles
{
    public const string Manager = "Inventory Manager";
    public const string StockClerk = "Inventory Stock Clerk";
    public const string SalesClerk = "Inventory Sales Clerk";
    public const string Viewer = "Inventory Viewer";
}
