using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;
using Repair.Domain;
using Repair.Infrastructure;
using Xunit;

namespace Repair.Tests;

/// <summary>
/// Purchases are the only thing that sets a part's cost, and that cost is what
/// every margin figure in the reports is measured against. Worth pinning down.
/// </summary>
public class PurchaseCostTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_repair_test_{Guid.NewGuid():N}"[..28];

    private RepairDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RepairDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

        _db = new RepairDbContext(options, new TestUser());

        try
        {
            await _db.Database.EnsureCreatedAsync();
            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_available) await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    private async Task<(Supplier Supplier, Part Part)> SeedAsync()
    {
        var supplier = new Supplier { Name = "Gulberg Electric Co" };
        var part = new Part { Sku = "ALT-15K", Name = "Alternator rewind kit", Price = 52000 };
        _db.Suppliers.Add(supplier);
        _db.Parts.Add(part);
        await _db.SaveChangesAsync();
        return (supplier, part);
    }

    private PartPurchase Purchase(Supplier supplier, DateOnly on, params (Part Part, decimal Qty, decimal Cost)[] lines) =>
        new()
        {
            SupplierId = supplier.Id,
            PurchasedOn = on,
            Items = lines.Select(l => new PartPurchaseItem
            {
                PartId = l.Part.Id,
                Quantity = l.Qty,
                UnitCost = l.Cost
            }).ToList()
        };

    [SkippableFact]
    public async Task A_purchase_sets_the_parts_last_and_average_cost()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 6, 15), (part, 2, 34000)), "u", "Tester");

        var stored = await _db.Parts.AsNoTracking().SingleAsync(p => p.Id == part.Id);

        Assert.Equal(34000m, stored.LastPurchaseCost);
        Assert.Equal(34000m, stored.AverageCost);
        Assert.Equal(2m, stored.PurchasedQuantity);
        Assert.Equal(new DateOnly(2026, 6, 15), stored.LastPurchasedOn);
        Assert.Equal(supplier.Id, stored.LastSupplierId);
    }

    [SkippableFact]
    public async Task The_average_is_weighted_by_quantity_not_a_plain_mean()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        // 2 at 34,000 then 1 at 36,000. A plain mean of the two prices would say
        // 35,000; weighted by quantity it is 34,666.67, which is what was paid.
        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 6, 15), (part, 2, 34000)), "u", "Tester");
        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 7, 10), (part, 1, 36000)), "u", "Tester");

        var stored = await _db.Parts.AsNoTracking().SingleAsync(p => p.Id == part.Id);

        Assert.Equal(36000m, stored.LastPurchaseCost);
        Assert.Equal(3m, stored.PurchasedQuantity);
        Assert.Equal(34666.6667m, stored.AverageCost);
    }

    [SkippableFact]
    public async Task An_older_invoice_entered_late_does_not_overwrite_the_newer_cost()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 7, 10), (part, 1, 36000)), "u", "Tester");

        // A June invoice found in a drawer and typed in during August.
        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 6, 15), (part, 2, 34000)), "u", "Tester");

        var stored = await _db.Parts.AsNoTracking().SingleAsync(p => p.Id == part.Id);

        Assert.Equal(36000m, stored.LastPurchaseCost);
        Assert.Equal(new DateOnly(2026, 7, 10), stored.LastPurchasedOn);
        // The average still counts both, because both were genuinely bought.
        Assert.Equal(34666.6667m, stored.AverageCost);
    }

    [SkippableFact]
    public async Task A_line_can_reprice_the_part_it_bought()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        var purchase = Purchase(supplier, new DateOnly(2026, 7, 10), (part, 1, 36000));
        purchase.Items[0].NewSellingPrice = 58000;

        await service.ReceiveAsync(purchase, "u", "Tester");

        var stored = await _db.Parts.AsNoTracking().SingleAsync(p => p.Id == part.Id);

        Assert.Equal(58000m, stored.Price);
        Assert.Equal(36000m, stored.LastPurchaseCost);
        // 58,000 sold on 36,000 cost is a 61.1% margin.
        Assert.Equal(61.11m, stored.MarginPercent);
    }

    [SkippableFact]
    public async Task Totals_carry_tax_discount_and_freight()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        var purchase = Purchase(supplier, new DateOnly(2026, 7, 10), (part, 2, 34000));
        purchase.DiscountAmount = 5000;
        purchase.TaxAmount = 3000;
        purchase.OtherCharges = 1500;

        var saved = await service.ReceiveAsync(purchase, "u", "Tester");

        Assert.Equal(68000m, saved.Subtotal);
        Assert.Equal(67500m, saved.TotalAmount);
        Assert.StartsWith("PUR-", saved.PurchaseNumber);
    }

    [SkippableFact]
    public async Task Price_history_reads_back_in_date_order()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 6, 15), (part, 2, 34000)), "u", "Tester");
        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 7, 10), (part, 1, 36000)), "u", "Tester");

        var history = await service.GetPriceHistoryAsync(part.Id);

        Assert.Equal(2, history.Count);
        Assert.Equal(34000m, history[0].UnitCost);
        Assert.Equal(36000m, history[1].UnitCost);
        Assert.Equal("Gulberg Electric Co", history[0].SupplierName);
    }

    [SkippableFact]
    public async Task Recalculating_rebuilds_costs_from_the_purchase_ledger()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        await service.ReceiveAsync(
            Purchase(supplier, new DateOnly(2026, 6, 15), (part, 2, 34000)), "u", "Tester");

        // Simulate the figures being wrong — an old import, or a restored backup.
        var stored = await _db.Parts.SingleAsync(p => p.Id == part.Id);
        stored.AverageCost = 999;
        stored.LastPurchaseCost = 999;
        stored.PurchasedQuantity = 0;
        await _db.SaveChangesAsync();

        await service.RecalculateCostsAsync();

        var fixedUp = await _db.Parts.AsNoTracking().SingleAsync(p => p.Id == part.Id);
        Assert.Equal(34000m, fixedUp.AverageCost);
        Assert.Equal(34000m, fixedUp.LastPurchaseCost);
        Assert.Equal(2m, fixedUp.PurchasedQuantity);
    }

    [SkippableFact]
    public async Task A_purchase_with_no_lines_is_refused()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, _) = await SeedAsync();
        var service = new PurchaseService(_db);

        var purchase = new PartPurchase { SupplierId = supplier.Id };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReceiveAsync(purchase, "u", "Tester"));
    }

    [SkippableFact]
    public async Task A_negative_quantity_is_refused()
    {
        Skip.IfNot(_available, "No database available");
        var (supplier, part) = await SeedAsync();
        var service = new PurchaseService(_db);

        var purchase = Purchase(supplier, new DateOnly(2026, 7, 10), (part, -1, 34000));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReceiveAsync(purchase, "u", "Tester"));
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
