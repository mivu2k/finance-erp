using ErpPlatform.Shared.Kernel;
using ErpPlatform.TestSupport;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Inventory.Tests;

/// <summary>
/// Optimistic locking on the stock-bearing records. Without it, two people editing the
/// same item silently last-write-wins and one of them loses their work with no warning
/// — which on a quantity means the shelf figure is simply wrong.
/// </summary>
public class ConcurrencyTests : IAsyncLifetime
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

    private async Task<int> SeedModelAsync()
    {
        await using var db = NewDb();
        var products = new ProductService(db);
        var product = await products.CreateAsync(new Product { Name = "Cable" });
        var model = await products.AddModelAsync(product.Id,
            new ProductModel { Name = "3-core", Unit = "m", SalePrice = 25 });
        return model.Id;
    }

    [SkippableFact]
    public async Task The_second_of_two_concurrent_edits_is_rejected()
    {
        IntegrationDatabase.Require(_available);
        var modelId = await SeedModelAsync();

        // Two users load the same row, as two independent requests would.
        await using var first = NewDb();
        await using var second = NewDb();

        var a = await first.ProductModels.FirstAsync(m => m.Id == modelId);
        var b = await second.ProductModels.FirstAsync(m => m.Id == modelId);

        a.SalePrice = 30;
        await first.SaveChangesAsync();

        // b still holds the stamp it read before A saved, so this must not go through.
        b.SalePrice = 40;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var check = NewDb();
        var winner = await check.ProductModels.FirstAsync(m => m.Id == modelId);
        Assert.Equal(30, winner.SalePrice);   // the first write stands, not the last
    }

    [SkippableFact]
    public async Task The_stamp_moves_on_every_save()
    {
        IntegrationDatabase.Require(_available);
        var modelId = await SeedModelAsync();

        Guid before, after;
        await using (var db = NewDb())
        {
            var model = await db.ProductModels.FirstAsync(m => m.Id == modelId);
            before = model.ConcurrencyStamp;
            model.SalePrice = 33;
            await db.SaveChangesAsync();
            after = model.ConcurrencyStamp;
        }

        Assert.NotEqual(Guid.Empty, before);
        Assert.NotEqual(before, after);
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
