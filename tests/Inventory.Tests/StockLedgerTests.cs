using ErpPlatform.Shared.Kernel;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Inventory.Tests;

/// <summary>
/// The stock ledger is the whole point of the module: the cached quantity on a
/// model or accessory must always be reconstructible from the transaction rows,
/// and nothing may be deleted while it still holds stock. Integration tests —
/// they create and drop a throwaway database and skip when no server is reachable.
/// </summary>
public class StockLedgerTests : IAsyncLifetime
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

    /// <summary>Seeds one product with one model and one accessory under it.</summary>
    private async Task<(int ProductId, int ModelId, int AccessoryId)> SeedAsync()
    {
        await using var db = NewDb();
        var products = new ProductService(db);

        var product = await products.CreateAsync(new Product { Name = "Laptop", Category = "IT" });
        var model = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "ModelX1", Unit = "pcs", ReorderThreshold = 2 });
        var accessory = await products.AddAccessoryAsync(model.Id,
            new Accessory { Name = "Charger", Unit = "pcs" });

        return (product.Id, model.Id, accessory.Id);
    }

    [Fact]
    public async Task Stock_in_and_out_moves_the_cached_quantity_and_writes_the_ledger()
    {
        if (!_available) return;
        var (_, modelId, _) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);

        var after10 = await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 10,
            StockReason.Purchase, "PO-1", null, "u1", "Tester");
        var after7 = await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.Out, 3,
            StockReason.Sale, null, null, "u1", "Tester");

        Assert.Equal(10, after10);
        Assert.Equal(7, after7);

        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == modelId);
        Assert.Equal(7, model.CurrentQuantity);

        // Every movement is one immutable row carrying the running balance.
        var ledger = await stock.ListTransactionsAsync(
            new StockFilter(StockItemType.Model, modelId));
        Assert.Equal(2, ledger.Count);
        Assert.Contains(ledger, t => t.Direction == StockDirection.In && t.BalanceAfter == 10);
        Assert.Contains(ledger, t => t.Direction == StockDirection.Out && t.BalanceAfter == 7);
    }

    [Fact]
    public async Task Stock_out_beyond_what_is_held_is_refused()
    {
        if (!_available) return;
        var (_, modelId, _) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);
        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 5,
            StockReason.Purchase, null, null, "u1", "Tester");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.Out, 6,
                StockReason.Sale, null, null, "u1", "Tester"));

        // The refused movement leaves nothing behind.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == modelId);
        Assert.Equal(5, model.CurrentQuantity);
    }

    [Fact]
    public async Task Zero_or_negative_quantities_are_refused()
    {
        if (!_available) return;
        var (_, modelId, _) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 0,
                StockReason.Adjustment, null, null, "u1", "Tester"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, -4,
                StockReason.Adjustment, null, null, "u1", "Tester"));
    }

    [Fact]
    public async Task Accessory_stock_is_tracked_independently_of_its_model()
    {
        if (!_available) return;
        var (_, modelId, accessoryId) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);
        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 4,
            StockReason.Purchase, null, null, "u1", "Tester");
        await stock.AdjustAsync(StockItemType.Accessory, accessoryId, StockDirection.In, 9,
            StockReason.Purchase, null, null, "u1", "Tester");

        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == modelId);
        var accessory = await db.Accessories.AsNoTracking().FirstAsync(a => a.Id == accessoryId);

        Assert.Equal(4, model.CurrentQuantity);
        Assert.Equal(9, accessory.CurrentQuantity);
    }

    [Fact]
    public async Task Recalculate_rebuilds_cached_quantities_from_the_ledger()
    {
        if (!_available) return;
        var (_, modelId, _) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);
        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 12,
            StockReason.Purchase, null, null, "u1", "Tester");
        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.Out, 5,
            StockReason.Sale, null, null, "u1", "Tester");

        // Corrupt the cache the way a bad write or a crashed job might.
        var model = await db.ProductModels.FirstAsync(m => m.Id == modelId);
        model.CurrentQuantity = 999;
        await db.SaveChangesAsync();

        await stock.RecalculateAsync();

        var repaired = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == modelId);
        Assert.Equal(7, repaired.CurrentQuantity);
    }

    [Fact]
    public async Task Low_stock_lists_what_sits_at_or_under_its_threshold()
    {
        if (!_available) return;
        var (_, modelId, _) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);

        // Threshold is 2; at 2 it counts as low, at 3 it does not.
        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 2,
            StockReason.Purchase, null, null, "u1", "Tester");
        Assert.Contains(await stock.ListLowStockAsync(),
            x => x.Type == StockItemType.Model && x.ItemId == modelId);

        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 1,
            StockReason.Purchase, null, null, "u1", "Tester");
        Assert.DoesNotContain(await stock.ListLowStockAsync(),
            x => x.Type == StockItemType.Model && x.ItemId == modelId);
    }

    [Fact]
    public async Task A_model_holding_stock_cannot_be_deleted()
    {
        if (!_available) return;
        var (_, modelId, _) = await SeedAsync();

        await using var db = NewDb();
        await new StockService(db).AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 3,
            StockReason.Purchase, null, null, "u1", "Tester");

        var products = new ProductService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.DeleteModelAsync(modelId));

        Assert.NotNull(await products.GetModelAsync(modelId));
    }

    [Fact]
    public async Task An_emptied_model_can_be_deleted_and_takes_its_accessories_with_it()
    {
        if (!_available) return;
        var (_, modelId, accessoryId) = await SeedAsync();

        await using var db = NewDb();
        var stock = new StockService(db);
        var products = new ProductService(db);

        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.In, 3,
            StockReason.Purchase, null, null, "u1", "Tester");
        await stock.AdjustAsync(StockItemType.Model, modelId, StockDirection.Out, 3,
            StockReason.Sale, null, null, "u1", "Tester");

        await products.DeleteModelAsync(modelId);

        Assert.Null(await products.GetModelAsync(modelId));
        Assert.Null(await products.GetAccessoryAsync(accessoryId));

        // Soft delete: the ledger still resolves, so history is not orphaned.
        var ledger = await stock.ListTransactionsAsync(new StockFilter(StockItemType.Model, modelId));
        Assert.Equal(2, ledger.Count);
    }

    [Fact]
    public async Task A_product_whose_accessory_holds_stock_cannot_be_deleted()
    {
        if (!_available) return;
        var (productId, _, accessoryId) = await SeedAsync();

        await using var db = NewDb();
        await new StockService(db).AdjustAsync(StockItemType.Accessory, accessoryId, StockDirection.In, 1,
            StockReason.Purchase, null, null, "u1", "Tester");

        var products = new ProductService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.DeleteAsync(productId));

        Assert.NotNull(await products.GetAsync(productId));
    }

    [Fact]
    public async Task An_empty_product_deletes_with_its_models_and_accessories()
    {
        if (!_available) return;
        var (productId, modelId, accessoryId) = await SeedAsync();

        await using var db = NewDb();
        var products = new ProductService(db);
        await products.DeleteAsync(productId);

        Assert.Null(await products.GetAsync(productId));
        Assert.Null(await products.GetModelAsync(modelId));
        Assert.Null(await products.GetAccessoryAsync(accessoryId));
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
