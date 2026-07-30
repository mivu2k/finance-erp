using ErpPlatform.Shared.Kernel;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Inventory.Tests;

/// <summary>
/// Suppliers, purchase orders and goods received notes. The rule underneath is that
/// an order is a commitment and a receipt is a movement: nothing reaches stock until
/// goods are booked in, so a paper order can never inflate what is on the shelf.
/// </summary>
public class PurchasingTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_inventory_test_{Guid.NewGuid():N}"[..30];
    private bool _available;

    private DbContextOptions<InventoryDbContext> Opts() =>
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

    private InventoryDbContext NewDb() => new(Opts(), new TestUser());

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { await db.Database.EnsureCreatedAsync(); _available = true; }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
    }

    private static (IPurchaseOrderService Orders, IGoodsReceiptService Receipts, IStockService Stock)
        Services(InventoryDbContext db)
    {
        var stock = new StockService(db);
        var tracking = new StockTrackingService(db, stock);
        return (new PurchaseOrderService(db), new GoodsReceiptService(db, tracking), stock);
    }

    private async Task<(int SupplierId, int PlainId, int SerialId, int WarehouseId)> SeedAsync()
    {
        await using var db = NewDb();
        var suppliers = new SupplierService(db);
        var products = new ProductService(db);
        var warehouses = new WarehouseService(db);

        var supplier = await suppliers.SaveAsync(new Supplier { Name = "Karachi Electric Supplies" });
        var warehouse = await warehouses.SaveAsync(new Warehouse { Name = "Main Store", IsDefault = true });

        var product = await products.CreateAsync(new Product { Name = "Cable" });
        var plain = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "3-core", Unit = "m" });
        var serialised = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "Meter", Unit = "pcs", IsSerialised = true });

        return (supplier.Id, plain.Id, serialised.Id, warehouse.Id);
    }

    private static PurchaseOrder Order(int supplierId, int warehouseId, int itemId,
        decimal qty, decimal cost) => new()
    {
        SupplierId = supplierId,
        WarehouseId = warehouseId,
        Date = new DateOnly(2026, 7, 30),
        Lines =
        [
            new PurchaseOrderLine
            {
                ItemType = StockItemType.Model, ItemId = itemId,
                ItemName = "Cable › 3-core", Quantity = qty, UnitCost = cost
            }
        ]
    };

    [Fact]
    public async Task An_order_totals_up_from_its_lines_and_charges()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (orders, _, _) = Services(db);

        var order = Order(supplierId, warehouseId, plainId, 100, 25);
        order.TaxAmount = 300;
        order.OtherCharges = 200;
        order.DiscountAmount = 100;

        var saved = await orders.SaveAsync(order, "u1", "Tester");

        Assert.Equal(2_500, saved.Subtotal);
        Assert.Equal(2_900, saved.TotalAmount);      // 2500 - 100 + 300 + 200
        Assert.Equal(PurchaseOrderStatus.Draft, saved.Status);
        Assert.StartsWith("PO-", saved.OrderNumber);
    }

    [Fact]
    public async Task Placing_an_order_moves_no_stock_at_all()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (orders, _, _) = Services(db);

        var order = await orders.SaveAsync(Order(supplierId, warehouseId, plainId, 100, 25), "u1", "Tester");
        await orders.PlaceAsync(order.Id);

        // A commitment is not a movement.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == plainId);
        Assert.Equal(0, model.CurrentQuantity);
    }

    [Fact]
    public async Task A_receipt_against_an_order_books_stock_and_draws_the_order_down()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (orders, receipts, stock) = Services(db);

        var order = await orders.SaveAsync(Order(supplierId, warehouseId, plainId, 100, 25), "u1", "Tester");
        await orders.PlaceAsync(order.Id);

        var grn = await receipts.BuildFromOrderAsync(order.Id);
        Assert.Equal(100, grn.Lines[0].Quantity);   // pre-filled with what is owed

        var saved = await receipts.SaveAsync(grn, "u1", "Tester");
        await receipts.PostAsync(saved.Id, "u1", "Tester");

        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == plainId);
        Assert.Equal(100, model.CurrentQuantity);
        Assert.Equal(25, model.AverageCost);        // cost came in with the goods

        // It landed in the warehouse the order named.
        var balances = await stock.ListBalancesAsync(StockItemType.Model, plainId);
        Assert.Equal(100, balances.Single(b => b.WarehouseId == warehouseId).Quantity);

        var after = await orders.GetAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.Received, after!.Status);
        Assert.Equal(0, after.Lines[0].Outstanding);
    }

    [Fact]
    public async Task A_part_delivery_leaves_the_order_partly_received()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (orders, receipts, _) = Services(db);

        var order = await orders.SaveAsync(Order(supplierId, warehouseId, plainId, 100, 25), "u1", "Tester");
        await orders.PlaceAsync(order.Id);

        var grn = await receipts.BuildFromOrderAsync(order.Id);
        grn.Lines[0].Quantity = 40;
        await receipts.PostAsync((await receipts.SaveAsync(grn, "u1", "Tester")).Id, "u1", "Tester");

        var after = await orders.GetAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.PartlyReceived, after!.Status);
        Assert.Equal(60, after.Lines[0].Outstanding);

        // The next receipt is pre-filled with only what is still owed.
        var second = await receipts.BuildFromOrderAsync(order.Id);
        Assert.Equal(60, second.Lines[0].Quantity);

        await receipts.PostAsync((await receipts.SaveAsync(second, "u1", "Tester")).Id, "u1", "Tester");

        var finished = await orders.GetAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.Received, finished!.Status);
        Assert.DoesNotContain(await orders.OutstandingAsync(), o => o.Id == order.Id);
    }

    [Fact]
    public async Task A_receipt_for_a_serialised_item_carries_its_serials_into_stock()
    {
        if (!_available) return;
        var (supplierId, _, serialId, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (_, receipts, stock) = Services(db);
        var tracking = new StockTrackingService(db, stock);

        var grn = new GoodsReceipt
        {
            SupplierId = supplierId, WarehouseId = warehouseId,
            Date = new DateOnly(2026, 7, 30),
            Lines =
            [
                new GoodsReceiptLine
                {
                    ItemType = StockItemType.Model, ItemId = serialId, ItemName = "Cable › Meter",
                    Quantity = 2, UnitCost = 9_000, SerialNumbers = "MTR-1, MTR-2"
                }
            ]
        };

        await receipts.PostAsync((await receipts.SaveAsync(grn, "u1", "Tester")).Id, "u1", "Tester");

        var units = await tracking.ListUnitsAsync(StockItemType.Model, serialId);
        Assert.Equal(2, units.Count);
        Assert.All(units, u => Assert.Equal(warehouseId, u.WarehouseId));
        Assert.Contains(units, u => u.SerialNumber == "MTR-1");
    }

    [Fact]
    public async Task A_serialised_receipt_still_has_to_balance_serials_against_quantity()
    {
        if (!_available) return;
        var (supplierId, _, serialId, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (_, receipts, _) = Services(db);

        var grn = new GoodsReceipt
        {
            SupplierId = supplierId, WarehouseId = warehouseId,
            Lines =
            [
                new GoodsReceiptLine
                {
                    ItemType = StockItemType.Model, ItemId = serialId, ItemName = "Cable › Meter",
                    Quantity = 3, UnitCost = 9_000, SerialNumbers = "MTR-1, MTR-2"
                }
            ]
        };

        var saved = await receipts.SaveAsync(grn, "u1", "Tester");

        // Routed through the same tracking rules however goods arrive.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receipts.PostAsync(saved.Id, "u1", "Tester"));
    }

    [Fact]
    public async Task A_posted_receipt_is_final_and_a_placed_order_is_locked()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (orders, receipts, _) = Services(db);

        var order = await orders.SaveAsync(Order(supplierId, warehouseId, plainId, 10, 25), "u1", "Tester");
        await orders.PlaceAsync(order.Id);

        // Editing after placing would change what the supplier was told.
        order.Lines[0].Quantity = 50;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orders.SaveAsync(order, "u1", "Tester"));

        var grn = await receipts.BuildFromOrderAsync(order.Id);
        var saved = await receipts.SaveAsync(grn, "u1", "Tester");
        await receipts.PostAsync(saved.Id, "u1", "Tester");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receipts.PostAsync(saved.Id, "u1", "Tester"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => receipts.CancelAsync(saved.Id));

        // And the order can't be cancelled once goods have landed against it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.CancelAsync(order.Id));
    }

    [Fact]
    public async Task An_order_with_nothing_left_cannot_be_received_again()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (orders, receipts, _) = Services(db);

        var order = await orders.SaveAsync(Order(supplierId, warehouseId, plainId, 5, 10), "u1", "Tester");
        await orders.PlaceAsync(order.Id);
        await receipts.PostAsync(
            (await receipts.SaveAsync(await receipts.BuildFromOrderAsync(order.Id), "u1", "Tester")).Id,
            "u1", "Tester");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receipts.BuildFromOrderAsync(order.Id));
    }

    [Fact]
    public async Task A_draft_order_cannot_be_received_against()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (orders, receipts, _) = Services(db);

        var order = await orders.SaveAsync(Order(supplierId, warehouseId, plainId, 5, 10), "u1", "Tester");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receipts.BuildFromOrderAsync(order.Id));
    }

    [Fact]
    public async Task A_supplier_with_history_cannot_be_deleted()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var suppliers = new SupplierService(db);
        var (orders, _, _) = Services(db);

        var spare = await suppliers.SaveAsync(new Supplier { Name = "Unused Traders" });
        await suppliers.DeleteAsync(spare.Id);
        Assert.Null(await suppliers.GetAsync(spare.Id));

        await orders.SaveAsync(Order(supplierId, warehouseId, plainId, 1, 1), "u1", "Tester");
        await Assert.ThrowsAsync<InvalidOperationException>(() => suppliers.DeleteAsync(supplierId));
    }

    [Fact]
    public async Task A_standalone_receipt_needs_no_order_behind_it()
    {
        if (!_available) return;
        var (supplierId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (_, receipts, _) = Services(db);

        var grn = new GoodsReceipt
        {
            SupplierId = supplierId, WarehouseId = warehouseId,
            SupplierDocumentNumber = "INV-9912",
            Lines =
            [
                new GoodsReceiptLine
                {
                    ItemType = StockItemType.Model, ItemId = plainId,
                    ItemName = "Cable › 3-core", Quantity = 20, UnitCost = 30
                }
            ]
        };

        await receipts.PostAsync((await receipts.SaveAsync(grn, "u1", "Tester")).Id, "u1", "Tester");

        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == plainId);
        Assert.Equal(20, model.CurrentQuantity);
    }

    private sealed class TestUser : ICurrentUserService
    {
        public string? UserId => "test";
        public string? UserName => "test";
        public string? IpAddress => null;
        public string? Browser => null;
        public bool HasPermission(string permission) => true;
    }
}
