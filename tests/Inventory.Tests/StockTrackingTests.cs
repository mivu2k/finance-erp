using ErpPlatform.Shared.Kernel;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Inventory.Tests;

/// <summary>
/// Serial tracking, batches, weighted-average costing and stock takes. The rule these
/// all hang off is that quantity only ever moves through the ledger, so serials,
/// batches and count variances must agree with it rather than write stock themselves.
/// </summary>
public class StockTrackingTests : IAsyncLifetime
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

    /// <summary>A serialised model, a batch-tracked one, and a plain accessory.</summary>
    private async Task<(int Serialised, int Batched, int Plain)> SeedAsync()
    {
        await using var db = NewDb();
        var products = new ProductService(db);

        var product = await products.CreateAsync(new Product { Name = "Laptop" });

        var serialised = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "ModelX1", Unit = "pcs", IsSerialised = true, ReorderThreshold = 2 });
        var batched = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "Battery Pack", Unit = "pcs", IsBatchTracked = true });
        var plain = await products.AddAccessoryAsync(serialised.Id,
            new Accessory { Name = "Charger", Unit = "pcs" });

        return (serialised.Id, batched.Id, plain.Id);
    }

    private static (IStockTrackingService Tracking, IStockService Stock) Services(InventoryDbContext db)
    {
        var stock = new StockService(db);
        return (new StockTrackingService(db, stock), stock);
    }

    [Fact]
    public async Task Receiving_a_serialised_item_creates_one_unit_per_serial()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 3, UnitCost: 50_000,
            SerialNumbers: ["SN-1", "SN-2", "SN-3"]), "u1", "Tester");

        var units = await tracking.ListUnitsAsync(StockItemType.Model, serialised);
        Assert.Equal(3, units.Count);
        Assert.All(units, u => Assert.Equal(StockUnitStatus.InStock, u.Status));

        // The quantity still came through the ledger, so the two agree.
        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == serialised);
        Assert.Equal(3, model.CurrentQuantity);
        Assert.Equal(units.Count(u => u.CountsAsStock), model.CurrentQuantity);
    }

    [Fact]
    public async Task A_serialised_receipt_must_carry_one_serial_per_unit()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        // Too few serials for the quantity would leave the shelf and the ledger
        // disagreeing from the very first receipt.
        await Assert.ThrowsAsync<InvalidOperationException>(() => tracking.ReceiveAsync(
            new StockReceipt(StockItemType.Model, serialised, 3, SerialNumbers: ["SN-1"]),
            "u1", "Tester"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracking.ReceiveAsync(
            new StockReceipt(StockItemType.Model, serialised, 2, SerialNumbers: ["SN-1", "SN-1"]),
            "u1", "Tester"));
    }

    [Fact]
    public async Task The_same_serial_cannot_be_received_twice_for_one_item()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 1, SerialNumbers: ["SN-1"]), "u1", "Tester");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracking.ReceiveAsync(
            new StockReceipt(StockItemType.Model, serialised, 1, SerialNumbers: ["SN-1"]),
            "u1", "Tester"));
    }

    [Fact]
    public async Task A_non_serialised_item_refuses_serials()
    {
        if (!_available) return;
        var (_, _, plain) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracking.ReceiveAsync(
            new StockReceipt(StockItemType.Accessory, plain, 1, SerialNumbers: ["SN-9"]),
            "u1", "Tester"));
    }

    [Fact]
    public async Task Issuing_serials_moves_those_units_out_and_the_quantity_with_them()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 3, SerialNumbers: ["SN-1", "SN-2", "SN-3"]),
            "u1", "Tester");

        await tracking.IssueSerialsAsync(StockItemType.Model, serialised, ["SN-1", "SN-2"],
            StockUnitStatus.Sold, "Gulberg Textiles", "INV-1", "u1", "Tester");

        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == serialised);
        Assert.Equal(1, model.CurrentQuantity);

        var inStock = await tracking.ListUnitsAsync(StockItemType.Model, serialised, StockUnitStatus.InStock);
        Assert.Single(inStock);
        Assert.Equal("SN-3", inStock[0].SerialNumber);
    }

    [Fact]
    public async Task A_serial_already_out_of_stock_cannot_be_issued_again()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 1, SerialNumbers: ["SN-1"]), "u1", "Tester");
        await tracking.IssueSerialsAsync(StockItemType.Model, serialised, ["SN-1"],
            StockUnitStatus.Sold, null, null, "u1", "Tester");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracking.IssueSerialsAsync(
            StockItemType.Model, serialised, ["SN-1"], StockUnitStatus.Sold,
            null, null, "u1", "Tester"));

        // An unknown serial is refused too, rather than silently doing nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => tracking.IssueSerialsAsync(
            StockItemType.Model, serialised, ["SN-NOPE"], StockUnitStatus.Sold,
            null, null, "u1", "Tester"));
    }

    [Fact]
    public async Task A_serial_can_be_found_from_anywhere_which_is_what_a_scanner_needs()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);
        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 1, SerialNumbers: ["SN-FIND-ME"]), "u1", "Tester");

        var found = await tracking.FindBySerialAsync("SN-FIND-ME");
        Assert.NotNull(found);
        Assert.Equal(serialised, found!.ItemId);
    }

    [Fact]
    public async Task A_batch_tracked_item_opens_a_batch_and_needs_a_batch_number()
    {
        if (!_available) return;
        var (_, batched, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracking.ReceiveAsync(
            new StockReceipt(StockItemType.Model, batched, 10), "u1", "Tester"));

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, batched, 10, UnitCost: 900, BatchNumber: "LOT-A",
            ExpiresOn: new DateOnly(2026, 12, 31)), "u1", "Tester");

        var batches = await tracking.ListBatchesAsync(StockItemType.Model, batched);
        Assert.Single(batches);
        Assert.Equal("LOT-A", batches[0].BatchNumber);
        Assert.Equal(10, batches[0].RemainingQuantity);
    }

    [Fact]
    public async Task Expiring_batches_are_listed_before_they_lapse()
    {
        if (!_available) return;
        var (_, batched, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        var soon = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        var later = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(400));

        await tracking.ReceiveAsync(new StockReceipt(StockItemType.Model, batched, 5,
            BatchNumber: "LOT-SOON", ExpiresOn: soon), "u1", "Tester");
        await tracking.ReceiveAsync(new StockReceipt(StockItemType.Model, batched, 5,
            BatchNumber: "LOT-LATER", ExpiresOn: later), "u1", "Tester");

        var expiring = await tracking.ListExpiringAsync(30);
        Assert.Single(expiring);
        Assert.Equal("LOT-SOON", expiring[0].BatchNumber);
    }

    [Fact]
    public async Task Average_cost_is_quantity_weighted_not_a_simple_mean()
    {
        if (!_available) return;
        var (_, _, plain) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        // 100 at 50 then 1 at 500: a simple mean would say 275.
        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Accessory, plain, 100, UnitCost: 50), "u1", "Tester");
        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Accessory, plain, 1, UnitCost: 500), "u1", "Tester");

        var a = await db.Accessories.AsNoTracking().FirstAsync(x => x.Id == plain);
        Assert.Equal(101, a.PurchasedQuantity);
        Assert.Equal(500, a.LastPurchaseCost);
        Assert.Equal(Math.Round((100 * 50m + 1 * 500m) / 101, 4), a.AverageCost);
        Assert.Equal(Math.Round(101 * a.AverageCost!.Value, 4), Math.Round(a.StockValue, 4));
    }

    [Fact]
    public async Task Valuation_prices_stock_at_cost_and_flags_what_is_low()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, _) = Services(db);

        // Reorder threshold is 2, so one unit in stock reads as low.
        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 1, UnitCost: 40_000, SerialNumbers: ["SN-1"]),
            "u1", "Tester");

        var row = (await tracking.ValuationAsync())
            .Single(r => r.ItemType == StockItemType.Model && r.ItemId == serialised);

        Assert.Equal(1, row.Quantity);
        Assert.Equal(40_000, row.AverageCost);
        Assert.Equal(40_000, row.Value);
        Assert.True(row.IsLow);
    }

    // --- stock take ---

    [Fact]
    public async Task A_stock_take_snapshots_what_the_system_holds()
    {
        if (!_available) return;
        var (serialised, _, plain) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, stock) = Services(db);
        var counts = new StockCountService(db, stock);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 2, SerialNumbers: ["SN-1", "SN-2"]), "u1", "Tester");

        var count = await counts.StartAsync("Monthly", "u1", "Tester");

        Assert.Equal(StockCountStatus.Draft, count.Status);
        Assert.Equal(2, count.Lines.Single(l =>
            l.ItemType == StockItemType.Model && l.ItemId == serialised).SystemQuantity);
        Assert.Contains(count.Lines, l => l.ItemType == StockItemType.Accessory && l.ItemId == plain);
    }

    [Fact]
    public async Task Posting_a_count_writes_its_variances_through_the_ledger()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, stock) = Services(db);
        var counts = new StockCountService(db, stock);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 5, SerialNumbers: ["A", "B", "C", "D", "E"]),
            "u1", "Tester");

        var count = await counts.StartAsync(null, "u1", "Tester");
        var line = count.Lines.Single(l => l.ItemType == StockItemType.Model && l.ItemId == serialised);

        // Only four actually on the shelf.
        await counts.SaveCountsAsync(count.Id, new Dictionary<int, decimal?> { [line.Id] = 4 });
        await counts.MarkCountedAsync(count.Id);
        await counts.PostAsync(count.Id, "u1", "Tester");

        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == serialised);
        Assert.Equal(4, model.CurrentQuantity);

        // The correction is a ledger row like any other, not a silent overwrite.
        var ledger = await stock.ListTransactionsAsync(
            new StockFilter(StockItemType.Model, serialised));
        Assert.Contains(ledger, t => t.Reference == count.CountNumber
                                     && t.Direction == StockDirection.Out && t.Quantity == 1);

        var posted = await counts.GetAsync(count.Id);
        Assert.Equal(StockCountStatus.Posted, posted!.Status);
    }

    [Fact]
    public async Task An_uncounted_line_is_left_alone_rather_than_treated_as_zero()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, stock) = Services(db);
        var counts = new StockCountService(db, stock);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 5, SerialNumbers: ["A", "B", "C", "D", "E"]),
            "u1", "Tester");

        var count = await counts.StartAsync(null, "u1", "Tester");
        var line = count.Lines.First(l => l.ItemType == StockItemType.Accessory);

        // Count something else entirely; the serialised model is never looked at.
        await counts.SaveCountsAsync(count.Id, new Dictionary<int, decimal?> { [line.Id] = 0 });
        await counts.MarkCountedAsync(count.Id);
        await counts.PostAsync(count.Id, "u1", "Tester");

        var model = await db.ProductModels.AsNoTracking().FirstAsync(m => m.Id == serialised);
        Assert.Equal(5, model.CurrentQuantity);
    }

    [Fact]
    public async Task Two_counts_cannot_be_open_at_once_and_a_posted_one_cannot_be_cancelled()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, stock) = Services(db);
        var counts = new StockCountService(db, stock);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 1, SerialNumbers: ["A"]), "u1", "Tester");

        var first = await counts.StartAsync(null, "u1", "Tester");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => counts.StartAsync(null, "u1", "Tester"));

        var line = first.Lines.First();
        await counts.SaveCountsAsync(first.Id, new Dictionary<int, decimal?> { [line.Id] = line.SystemQuantity });
        await counts.MarkCountedAsync(first.Id);
        await counts.PostAsync(first.Id, "u1", "Tester");

        await Assert.ThrowsAsync<InvalidOperationException>(() => counts.CancelAsync(first.Id));
    }

    [Fact]
    public async Task A_count_cannot_be_posted_before_it_is_finished()
    {
        if (!_available) return;
        var (serialised, _, _) = await SeedAsync();

        await using var db = NewDb();
        var (tracking, stock) = Services(db);
        var counts = new StockCountService(db, stock);

        await tracking.ReceiveAsync(new StockReceipt(
            StockItemType.Model, serialised, 1, SerialNumbers: ["A"]), "u1", "Tester");

        var count = await counts.StartAsync(null, "u1", "Tester");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => counts.PostAsync(count.Id, "u1", "Tester"));

        // And nothing has been counted yet, so it can't be marked finished either.
        await Assert.ThrowsAsync<InvalidOperationException>(() => counts.MarkCountedAsync(count.Id));
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
