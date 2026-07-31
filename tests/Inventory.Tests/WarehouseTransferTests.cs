using ErpPlatform.TestSupport;
using ErpPlatform.Shared.Kernel;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Inventory.Tests;

/// <summary>
/// Warehouses and transfers. The point of the two-step transfer is the gap between
/// dispatch and receipt: goods that have left one store and not yet arrived at
/// another are real, and a one-step move would show them in two places or in none.
/// </summary>
public class WarehouseTransferTests : IAsyncLifetime
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
        if (!_available) return;   // nothing was created, so nothing to drop
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<(int ItemId, int Main, int Van)> SeedAsync()
    {
        await using var db = NewDb();
        var warehouses = new WarehouseService(db);
        var products = new ProductService(db);
        var stock = new StockService(db);

        var main = await warehouses.SaveAsync(new Warehouse { Name = "Main Store", IsDefault = true });
        var van = await warehouses.SaveAsync(new Warehouse { Name = "Service Van" });

        var product = await products.CreateAsync(new Product { Name = "Cable" });
        var model = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "3-core 2.5mm", Unit = "m" });

        // 100m into the main store.
        await stock.AdjustAsync(StockItemType.Model, model.Id, StockDirection.In, 100,
            StockReason.Purchase, null, null, "u1", "Tester", main.Id);

        return (model.Id, main.Id, van.Id);
    }

    private static IStockTransferService Transfers(InventoryDbContext db) =>
        new StockTransferService(db, new StockService(db));

    private static StockTransfer Draft(int itemId, int from, int to, decimal qty) => new()
    {
        FromWarehouseId = from, ToWarehouseId = to,
        Date = new DateOnly(2026, 7, 30),
        Lines =
        [
            new StockTransferLine
            {
                ItemType = StockItemType.Model, ItemId = itemId,
                ItemName = "Cable › 3-core 2.5mm", Quantity = qty
            }
        ]
    };

    [SkippableFact]
    public async Task Stock_lands_in_the_warehouse_it_was_received_into()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, van) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);

        var balances = await stock.ListBalancesAsync(StockItemType.Model, itemId);
        Assert.Single(balances);
        Assert.Equal(main, balances[0].WarehouseId);
        Assert.Equal(100, balances[0].Quantity);

        // The item total is the sum across locations, so both stay usable.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == itemId);
        Assert.Equal(100, model.CurrentQuantity);
        Assert.NotEqual(main, van);
    }

    [SkippableFact]
    public async Task Dispatch_removes_from_the_source_and_leaves_the_goods_in_transit()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, van) = await SeedAsync();

        await using var db = NewDb();
        var transfers = Transfers(db);
        var stock = new StockService(db);

        var transfer = await transfers.CreateAsync(Draft(itemId, main, van, 30), "u1", "Tester");
        await transfers.DispatchAsync(transfer.Id, "u1", "Tester", "Storeman");

        var after = await transfers.GetAsync(transfer.Id);
        Assert.Equal(StockTransferStatus.InTransit, after!.Status);

        var balances = await stock.ListBalancesAsync(StockItemType.Model, itemId);
        Assert.Equal(70, balances.Single(b => b.WarehouseId == main).Quantity);

        // Nothing has arrived yet — the van holds nothing.
        Assert.DoesNotContain(balances, b => b.WarehouseId == van && b.Quantity != 0);

        // And the overall total is down, because in-transit stock is genuinely
        // in neither warehouse.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == itemId);
        Assert.Equal(70, model.CurrentQuantity);
    }

    [SkippableFact]
    public async Task Receiving_puts_the_goods_into_the_destination()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, van) = await SeedAsync();

        await using var db = NewDb();
        var transfers = Transfers(db);
        var stock = new StockService(db);

        var transfer = await transfers.CreateAsync(Draft(itemId, main, van, 30), "u1", "Tester");
        await transfers.DispatchAsync(transfer.Id, "u1", "Tester", null);
        await transfers.ReceiveAsync(transfer.Id, new Dictionary<int, decimal>(), "u1", "Tester", null);

        var balances = await stock.ListBalancesAsync(StockItemType.Model, itemId);
        Assert.Equal(70, balances.Single(b => b.WarehouseId == main).Quantity);
        Assert.Equal(30, balances.Single(b => b.WarehouseId == van).Quantity);

        // Back to where it started overall: nothing was created or destroyed.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == itemId);
        Assert.Equal(100, model.CurrentQuantity);
    }

    [SkippableFact]
    public async Task Receiving_short_is_allowed_and_the_shortfall_is_recorded()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, van) = await SeedAsync();

        await using var db = NewDb();
        var transfers = Transfers(db);
        var stock = new StockService(db);

        var transfer = await transfers.CreateAsync(Draft(itemId, main, van, 30), "u1", "Tester");
        await transfers.DispatchAsync(transfer.Id, "u1", "Tester", null);

        var lineId = transfer.Lines[0].Id;
        await transfers.ReceiveAsync(transfer.Id,
            new Dictionary<int, decimal> { [lineId] = 28 }, "u1", "Tester", null);

        var after = await transfers.GetAsync(transfer.Id);
        var line = after!.Lines[0];
        Assert.Equal(28, line.ReceivedQuantity);
        Assert.Equal(-2, line.Shortfall);

        var balances = await stock.ListBalancesAsync(StockItemType.Model, itemId);
        Assert.Equal(28, balances.Single(b => b.WarehouseId == van).Quantity);

        // The missing 2 are gone from the books, which is exactly what a shortfall
        // means — it is visible rather than quietly absorbed.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == itemId);
        Assert.Equal(98, model.CurrentQuantity);
    }

    [SkippableFact]
    public async Task More_cannot_arrive_than_was_sent()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, van) = await SeedAsync();

        await using var db = NewDb();
        var transfers = Transfers(db);

        var transfer = await transfers.CreateAsync(Draft(itemId, main, van, 10), "u1", "Tester");
        await transfers.DispatchAsync(transfer.Id, "u1", "Tester", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.ReceiveAsync(
            transfer.Id, new Dictionary<int, decimal> { [transfer.Lines[0].Id] = 12 },
            "u1", "Tester", null));
    }

    [SkippableFact]
    public async Task A_transfer_cannot_move_more_than_the_source_holds()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, van) = await SeedAsync();

        await using var db = NewDb();
        var transfers = Transfers(db);

        var transfer = await transfers.CreateAsync(Draft(itemId, main, van, 500), "u1", "Tester");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transfers.DispatchAsync(transfer.Id, "u1", "Tester", null));
    }

    [SkippableFact]
    public async Task Source_and_destination_have_to_differ()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, _) = await SeedAsync();

        await using var db = NewDb();
        var transfers = Transfers(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transfers.CreateAsync(Draft(itemId, main, main, 5), "u1", "Tester"));
    }

    [SkippableFact]
    public async Task Goods_already_in_transit_cannot_be_cancelled_away()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, van) = await SeedAsync();

        await using var db = NewDb();
        var transfers = Transfers(db);

        var transfer = await transfers.CreateAsync(Draft(itemId, main, van, 10), "u1", "Tester");

        // A draft is fine to drop.
        var second = await transfers.CreateAsync(Draft(itemId, main, van, 5), "u1", "Tester");
        await transfers.CancelAsync(second.Id);

        // Once dispatched the stock has left the source, so cancelling would lose it.
        await transfers.DispatchAsync(transfer.Id, "u1", "Tester", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.CancelAsync(transfer.Id));

        await transfers.ReceiveAsync(transfer.Id, new Dictionary<int, decimal>(), "u1", "Tester", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.CancelAsync(transfer.Id));
    }

    [SkippableFact]
    public async Task A_warehouse_holding_stock_cannot_be_deleted()
    {
        IntegrationDatabase.Require(_available);
        var (_, main, van) = await SeedAsync();

        await using var db = NewDb();
        var warehouses = new WarehouseService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => warehouses.DeleteAsync(main));

        // The empty one goes without complaint.
        await warehouses.DeleteAsync(van);
        Assert.Null(await warehouses.GetAsync(van));
    }

    [SkippableFact]
    public async Task Exactly_one_warehouse_is_the_default()
    {
        IntegrationDatabase.Require(_available);
        var (_, main, van) = await SeedAsync();

        await using var db = NewDb();
        var warehouses = new WarehouseService(db);

        var vanRow = await warehouses.GetAsync(van);
        vanRow!.IsDefault = true;
        await warehouses.SaveAsync(vanRow);

        var all = await warehouses.ListAsync();
        Assert.Single(all.Where(w => w.IsDefault));
        Assert.Equal(van, all.Single(w => w.IsDefault).Id);
        Assert.False(all.Single(w => w.Id == main).IsDefault);
    }

    [SkippableFact]
    public async Task What_a_warehouse_holds_can_be_listed()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, _) = await SeedAsync();

        await using var db = NewDb();
        var rows = await new WarehouseService(db).StockAtAsync(main);

        var row = Assert.Single(rows);
        Assert.Equal(itemId, row.ItemId);
        Assert.Equal(100, row.Quantity);
        Assert.Contains("3-core", row.ItemName);
    }

    [SkippableFact]
    public async Task Recalculate_repairs_per_warehouse_balances_too()
    {
        IntegrationDatabase.Require(_available);
        var (itemId, main, _) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);

        // Corrupt the location cache the way a crashed job might.
        var balance = await db.StockBalances.FirstAsync(b => b.WarehouseId == main);
        balance.Quantity = 999;
        await db.SaveChangesAsync();

        await stock.RecalculateAsync();

        var repaired = await stock.ListBalancesAsync(StockItemType.Model, itemId);
        Assert.Equal(100, repaired.Single(b => b.WarehouseId == main).Quantity);
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
