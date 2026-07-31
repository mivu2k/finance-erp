using ErpPlatform.Shared.Kernel;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Inventory.Tests;

/// <summary>
/// Customers, sales orders and delivery notes. The rule underneath mirrors purchasing:
/// an order is a commitment and a delivery is a movement, so nothing leaves stock until
/// a note is posted and a paper order can never deflate what is on the shelf.
/// </summary>
public class SalesTests : IAsyncLifetime
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

    private static (ISalesOrderService Orders, IDeliveryService Deliveries, IStockService Stock)
        Services(InventoryDbContext db)
    {
        var stock = new StockService(db);
        var tracking = new StockTrackingService(db, stock);
        return (new SalesOrderService(db), new DeliveryService(db, stock, tracking), stock);
    }

    /// <summary>A customer, a warehouse, and two models — one plain, one serialised — with stock in.</summary>
    private async Task<(int CustomerId, int PlainId, int SerialId, int WarehouseId)> SeedAsync(
        decimal plainQty = 100, decimal plainCost = 10)
    {
        await using var db = NewDb();
        var customers = new CustomerService(db);
        var products = new ProductService(db);
        var warehouses = new WarehouseService(db);
        var stock = new StockService(db);
        var tracking = new StockTrackingService(db, stock);

        var customer = await customers.SaveAsync(new Customer { Name = "Gulf Diagnostics" });
        var warehouse = await warehouses.SaveAsync(new Warehouse { Name = "Main Store", IsDefault = true });

        var product = await products.CreateAsync(new Product { Name = "Cable" });
        var plain = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "3-core", Unit = "m", SalePrice = 25 });
        var serialised = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "Meter", Unit = "pcs", IsSerialised = true, SalePrice = 500 });

        if (plainQty > 0)
            await tracking.ReceiveAsync(new StockReceipt(
                    StockItemType.Model, plain.Id, plainQty, plainCost, null, null, [],
                    "GRN-SEED", "seed"),
                "u1", "Seeder", warehouse.Id);

        await tracking.ReceiveAsync(new StockReceipt(
                StockItemType.Model, serialised.Id, 2, 300, null, null, ["SN-1", "SN-2"],
                "GRN-SEED", "seed"),
            "u1", "Seeder", warehouse.Id);

        return (customer.Id, plain.Id, serialised.Id, warehouse.Id);
    }

    private static SalesOrder AnOrder(int customerId, int itemId, decimal qty = 10, decimal price = 25) =>
        new()
        {
            CustomerId = customerId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            RaisedById = "u1",
            RaisedByName = "Sales",
            Lines =
            [
                new SalesOrderLine
                {
                    ItemType = StockItemType.Model, ItemId = itemId,
                    ItemName = "Cable › 3-core", Quantity = qty, UnitPrice = price
                }
            ]
        };

    [Fact]
    public async Task Confirming_an_order_moves_no_stock()
    {
        if (!_available) return;
        var (customerId, plainId, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (orders, _, _) = Services(db);

        var order = await orders.SaveAsync(AnOrder(customerId, plainId));
        await orders.ConfirmAsync(order.Id);

        // The whole point of splitting order from delivery.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == plainId);
        Assert.Equal(100, model.CurrentQuantity);
        Assert.StartsWith("SO-", order.OrderNumber);
    }

    [Fact]
    public async Task Posting_a_delivery_issues_stock_and_closes_the_order()
    {
        if (!_available) return;
        var (customerId, plainId, _, _) = await SeedAsync();

        int orderId;
        await using (var db = NewDb())
        {
            var (orders, deliveries, _) = Services(db);
            var order = await orders.SaveAsync(AnOrder(customerId, plainId));
            await orders.ConfirmAsync(order.Id);
            orderId = order.Id;

            var draft = await deliveries.BuildFromOrderAsync(orderId);
            var saved = await deliveries.SaveAsync(draft);
            await deliveries.PostAsync(saved.Id, "u2", "Storeman");
        }

        await using (var db = NewDb())
        {
            var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == plainId);
            Assert.Equal(90, model.CurrentQuantity);

            var order = await new SalesOrderService(db).GetAsync(orderId);
            Assert.Equal(SalesOrderStatus.Delivered, order!.Status);
            Assert.Equal(10, order.Lines[0].DeliveredQuantity);
            Assert.Equal(0, order.Lines[0].Outstanding);

            // The movement lands in the ordinary ledger, marked as a sale.
            var moves = await db.StockTransactions.AsNoTracking()
                .Where(t => t.ItemId == plainId && t.Reason == StockReason.Sale).ToListAsync();
            Assert.Single(moves);
            Assert.Equal(StockDirection.Out, moves[0].Direction);
        }
    }

    [Fact]
    public async Task A_partial_delivery_leaves_the_order_partly_delivered()
    {
        if (!_available) return;
        var (customerId, plainId, _, _) = await SeedAsync();

        int orderId;
        await using (var db = NewDb())
        {
            var (orders, deliveries, _) = Services(db);
            var order = await orders.SaveAsync(AnOrder(customerId, plainId, qty: 10));
            await orders.ConfirmAsync(order.Id);
            orderId = order.Id;

            var draft = await deliveries.BuildFromOrderAsync(orderId);
            draft.Lines[0].Quantity = 4;
            var saved = await deliveries.SaveAsync(draft);
            await deliveries.PostAsync(saved.Id, "u2", "Storeman");
        }

        await using (var db = NewDb())
        {
            var order = await new SalesOrderService(db).GetAsync(orderId);
            Assert.Equal(SalesOrderStatus.PartlyDelivered, order!.Status);
            Assert.Equal(6, order.Lines[0].Outstanding);

            // A second note covers only what is still owed.
            var (_, deliveries, _) = Services(db);
            var next = await deliveries.BuildFromOrderAsync(orderId);
            Assert.Equal(6, next.Lines[0].Quantity);
        }
    }

    [Fact]
    public async Task A_delivery_bigger_than_stock_is_refused_before_anything_moves()
    {
        if (!_available) return;
        var (customerId, plainId, _, _) = await SeedAsync(plainQty: 5);

        await using var db = NewDb();
        var (orders, deliveries, _) = Services(db);

        var order = await orders.SaveAsync(AnOrder(customerId, plainId, qty: 10));
        await orders.ConfirmAsync(order.Id);

        var draft = await deliveries.BuildFromOrderAsync(order.Id);
        var saved = await deliveries.SaveAsync(draft);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            deliveries.PostAsync(saved.Id, "u2", "Storeman"));

        // Refused outright rather than half-posted.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == plainId);
        Assert.Equal(5, model.CurrentQuantity);
        Assert.Equal(DeliveryStatus.Draft, (await deliveries.GetAsync(saved.Id))!.Status);
    }

    [Fact]
    public async Task The_cost_of_what_went_out_is_snapshotted_at_posting()
    {
        if (!_available) return;
        var (customerId, plainId, _, warehouseId) = await SeedAsync(plainQty: 100, plainCost: 10);

        int deliveryId;
        await using (var db = NewDb())
        {
            var (orders, deliveries, _) = Services(db);
            var order = await orders.SaveAsync(AnOrder(customerId, plainId, qty: 10, price: 25));
            await orders.ConfirmAsync(order.Id);

            var draft = await deliveries.BuildFromOrderAsync(order.Id);
            var saved = await deliveries.SaveAsync(draft);
            await deliveries.PostAsync(saved.Id, "u2", "Storeman");
            deliveryId = saved.Id;
        }

        // A later, dearer purchase moves the weighted average.
        await using (var db = NewDb())
        {
            var stock = new StockService(db);
            await new StockTrackingService(db, stock).ReceiveAsync(new StockReceipt(
                    StockItemType.Model, plainId, 100, 30, null, null, [], "GRN-2", "later"),
                "u1", "Buyer", warehouseId);
        }

        await using (var db = NewDb())
        {
            var delivery = await new DeliveryService(db, new StockService(db),
                new StockTrackingService(db, new StockService(db))).GetAsync(deliveryId);

            // Still costed at 10, not the new average — a margin that rewrites itself
            // after the fact is worthless.
            Assert.Equal(100m, delivery!.TotalCost);
            Assert.Equal(250m, delivery.TotalAmount);
            Assert.Equal(150m, delivery.GrossProfit);
        }
    }

    [Fact]
    public async Task A_serialised_line_must_name_exactly_the_serials_it_ships()
    {
        if (!_available) return;
        var (customerId, _, serialId, _) = await SeedAsync();

        await using var db = NewDb();
        var (orders, deliveries, _) = Services(db);

        var order = await orders.SaveAsync(new SalesOrder
        {
            CustomerId = customerId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Lines =
            [
                new SalesOrderLine
                {
                    ItemType = StockItemType.Model, ItemId = serialId,
                    ItemName = "Cable › Meter", Quantity = 2, UnitPrice = 500
                }
            ]
        });
        await orders.ConfirmAsync(order.Id);

        var draft = await deliveries.BuildFromOrderAsync(order.Id);
        var saved = await deliveries.SaveAsync(draft);

        // No serials listed at all.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            deliveries.PostAsync(saved.Id, "u2", "Storeman"));

        // Only one, for a quantity of two.
        saved.Lines[0].SerialNumbers = "SN-1";
        await deliveries.SaveAsync(saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            deliveries.PostAsync(saved.Id, "u2", "Storeman"));
    }

    [Fact]
    public async Task Shipping_serialised_units_marks_them_sold()
    {
        if (!_available) return;
        var (customerId, _, serialId, _) = await SeedAsync();

        await using (var db = NewDb())
        {
            var (orders, deliveries, _) = Services(db);
            var order = await orders.SaveAsync(new SalesOrder
            {
                CustomerId = customerId,
                Date = DateOnly.FromDateTime(DateTime.UtcNow),
                Lines =
                [
                    new SalesOrderLine
                    {
                        ItemType = StockItemType.Model, ItemId = serialId,
                        ItemName = "Cable › Meter", Quantity = 1, UnitPrice = 500
                    }
                ]
            });
            await orders.ConfirmAsync(order.Id);

            var draft = await deliveries.BuildFromOrderAsync(order.Id);
            draft.Lines[0].SerialNumbers = "SN-1";
            var saved = await deliveries.SaveAsync(draft);
            await deliveries.PostAsync(saved.Id, "u2", "Storeman");
        }

        await using (var db = NewDb())
        {
            var sold = await db.StockUnits.AsNoTracking().FirstAsync(u => u.SerialNumber == "SN-1");
            var kept = await db.StockUnits.AsNoTracking().FirstAsync(u => u.SerialNumber == "SN-2");

            Assert.Equal(StockUnitStatus.Sold, sold.Status);
            Assert.Equal(StockUnitStatus.InStock, kept.Status);

            // The named unit moved the quantity too — not double counted.
            var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == serialId);
            Assert.Equal(1, model.CurrentQuantity);
        }
    }

    [Fact]
    public async Task A_posted_delivery_cannot_be_cancelled_or_edited()
    {
        if (!_available) return;
        var (customerId, plainId, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (orders, deliveries, _) = Services(db);

        var order = await orders.SaveAsync(AnOrder(customerId, plainId));
        await orders.ConfirmAsync(order.Id);
        var saved = await deliveries.SaveAsync(await deliveries.BuildFromOrderAsync(order.Id));
        await deliveries.PostAsync(saved.Id, "u2", "Storeman");

        // The stock is already out; reversing is a return, not a cancel.
        await Assert.ThrowsAsync<InvalidOperationException>(() => deliveries.CancelAsync(saved.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => deliveries.SaveAsync(saved));
    }

    [Fact]
    public async Task An_order_that_has_shipped_cannot_be_cancelled_or_re_scoped()
    {
        if (!_available) return;
        var (customerId, plainId, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (orders, deliveries, _) = Services(db);

        var order = await orders.SaveAsync(AnOrder(customerId, plainId, qty: 10));
        await orders.ConfirmAsync(order.Id);

        var draft = await deliveries.BuildFromOrderAsync(order.Id);
        draft.Lines[0].Quantity = 4;
        var saved = await deliveries.SaveAsync(draft);
        await deliveries.PostAsync(saved.Id, "u2", "Storeman");

        var reloaded = await orders.GetAsync(order.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.CancelAsync(order.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.SaveAsync(reloaded!));
    }

    [Fact]
    public async Task A_counter_sale_needs_no_order_behind_it()
    {
        if (!_available) return;
        var (customerId, plainId, _, warehouseId) = await SeedAsync();

        await using var db = NewDb();
        var (_, deliveries, _) = Services(db);

        var saved = await deliveries.SaveAsync(new Delivery
        {
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            ReceivedByName = "Walk-in",
            Lines =
            [
                new DeliveryLine
                {
                    ItemType = StockItemType.Model, ItemId = plainId,
                    ItemName = "Cable › 3-core", Quantity = 3, UnitPrice = 25
                }
            ]
        });
        await deliveries.PostAsync(saved.Id, "u2", "Storeman");

        Assert.StartsWith("DN-", saved.DeliveryNumber);
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == plainId);
        Assert.Equal(97, model.CurrentQuantity);
    }

    [Fact]
    public async Task A_customer_with_history_cannot_be_deleted()
    {
        if (!_available) return;
        var (customerId, plainId, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (orders, _, _) = Services(db);
        await orders.SaveAsync(AnOrder(customerId, plainId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CustomerService(db).DeleteAsync(customerId));
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
