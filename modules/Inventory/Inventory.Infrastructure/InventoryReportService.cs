using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

/// <param name="Key">Stable identifier used in the URL and the print endpoint.</param>
/// <param name="ShowsMoney">Needs the cost permission — gates the whole report.</param>
public record InventoryReportDefinition(
    string Key, string Name, string Description, bool ShowsMoney = false, bool Landscape = false);

/// <summary>
/// Every inventory report, built into the one <see cref="InventoryReport"/> shape.
/// </summary>
/// <remarks>
/// The screen and the PDF both render from the same built report, so they cannot show
/// different numbers — the same reason Repair shapes all of its reports through one
/// table builder.
/// </remarks>
public interface IInventoryReportService
{
    IReadOnlyList<InventoryReportDefinition> Catalog { get; }
    InventoryReportDefinition? Find(string key);

    /// <summary>Builds a report. Money columns are dropped when the caller can't see costs.</summary>
    Task<InventoryReport> BuildAsync(string key, bool includeMoney, CancellationToken ct = default);
}

public class InventoryReportService(
    InventoryDbContext db,
    IStockTrackingService tracking,
    IPurchaseOrderService orders) : IInventoryReportService
{
    public IReadOnlyList<InventoryReportDefinition> Catalog { get; } =
    [
        new("stock-levels", "Stock levels", "Everything on hand, with its reorder level."),
        new("valuation", "Stock valuation", "Stock at cost, with a total.", ShowsMoney: true, Landscape: true),
        new("low-stock", "Low stock", "At or below the reorder level — what to buy."),
        new("by-warehouse", "Stock by warehouse", "Where the stock actually is.", Landscape: true),
        new("serials", "Serialised units", "Every tracked unit and its status.", Landscape: true),
        new("batches", "Batches and expiry", "Open batches, oldest first."),
        new("movements", "Stock movements", "The ledger, most recent first.", Landscape: true),
        new("outstanding-po", "Outstanding orders", "Ordered but not yet received.", ShowsMoney: true)
    ];

    public InventoryReportDefinition? Find(string key) =>
        Catalog.FirstOrDefault(r => r.Key == key);

    public async Task<InventoryReport> BuildAsync(
        string key, bool includeMoney, CancellationToken ct = default)
    {
        var definition = Find(key)
            ?? throw new InvalidOperationException($"No such report: {key}.");

        var asAt = $"As at {DateTime.Now:yyyy-MM-dd HH:mm}";

        return key switch
        {
            "stock-levels" => await StockLevelsAsync(definition, asAt, includeMoney, ct),
            "valuation" => await ValuationAsync(definition, asAt, ct),
            "low-stock" => await LowStockAsync(definition, asAt, ct),
            "by-warehouse" => await ByWarehouseAsync(definition, asAt, ct),
            "serials" => await SerialsAsync(definition, asAt, includeMoney, ct),
            "batches" => await BatchesAsync(definition, asAt, includeMoney, ct),
            "movements" => await MovementsAsync(definition, asAt, ct),
            "outstanding-po" => await OutstandingOrdersAsync(definition, asAt, ct),
            _ => throw new InvalidOperationException($"No such report: {key}.")
        };
    }

    private async Task<InventoryReport> StockLevelsAsync(
        InventoryReportDefinition d, string asAt, bool money, CancellationToken ct)
    {
        var rows = await tracking.ValuationAsync(ct);

        List<ReportColumn> columns =
        [
            new("Product", 3), new("Item", 3), new("Unit", 1),
            new("On hand", 1.2f, true), new("Reorder at", 1.2f, true), new("Status", 1.2f)
        ];
        if (money) columns.Add(new ReportColumn("Sale price", 1.5f, true));

        return new InventoryReport(d.Name, asAt, columns,
            rows.Select(r =>
            {
                var cells = new List<string>
                {
                    r.ProductName, r.ItemName, r.Unit,
                    r.Quantity.ToString("0.##"), r.ReorderThreshold.ToString("0.##"),
                    r.IsLow ? "LOW" : "OK"
                };
                if (money) cells.Add(r.SalePrice.ToString("N2"));
                return cells.ToArray();
            }).ToList());
    }

    private async Task<InventoryReport> ValuationAsync(
        InventoryReportDefinition d, string asAt, CancellationToken ct)
    {
        var rows = await tracking.ValuationAsync(ct);
        var total = rows.Sum(r => r.Value);

        return new InventoryReport(d.Name, asAt,
        [
            new("Product", 3), new("Item", 3), new("Unit", 1),
            new("Qty", 1.2f, true), new("Avg cost", 1.5f, true),
            new("Value", 1.7f, true), new("Sale price", 1.5f, true)
        ],
            rows.Select(r => new[]
            {
                r.ProductName, r.ItemName, r.Unit, r.Quantity.ToString("0.##"),
                r.AverageCost?.ToString("N2") ?? "-", r.Value.ToString("N2"),
                r.SalePrice.ToString("N2")
            }).ToList(),
            ["", "", "", "", "Total", total.ToString("N2"), ""],
            "Valued at weighted-average cost, not sale price.");
    }

    private async Task<InventoryReport> LowStockAsync(
        InventoryReportDefinition d, string asAt, CancellationToken ct)
    {
        var rows = (await tracking.ValuationAsync(ct)).Where(r => r.IsLow).ToList();

        return new InventoryReport(d.Name, asAt,
        [
            new("Product", 3), new("Item", 3), new("Unit", 1),
            new("On hand", 1.2f, true), new("Reorder at", 1.2f, true), new("Short by", 1.2f, true)
        ],
            rows.Select(r => new[]
            {
                r.ProductName, r.ItemName, r.Unit, r.Quantity.ToString("0.##"),
                r.ReorderThreshold.ToString("0.##"),
                Math.Max(0, r.ReorderThreshold - r.Quantity).ToString("0.##")
            }).ToList());
    }

    private async Task<InventoryReport> ByWarehouseAsync(
        InventoryReportDefinition d, string asAt, CancellationToken ct)
    {
        var balances = await db.StockBalances.Include(b => b.Warehouse).AsNoTracking()
            .Where(b => b.Quantity != 0).ToListAsync(ct);
        var names = await ItemNamesAsync(ct);

        return new InventoryReport(d.Name, asAt,
        [
            new("Warehouse", 2.5f), new("Item", 5), new("Qty", 1.5f, true)
        ],
            balances
                .OrderBy(b => b.Warehouse.Name)
                .ThenBy(b => names.GetValueOrDefault((b.ItemType, b.ItemId), ""))
                .Select(b => new[]
                {
                    b.Warehouse.Name,
                    names.GetValueOrDefault((b.ItemType, b.ItemId), $"#{b.ItemId}"),
                    b.Quantity.ToString("0.##")
                }).ToList());
    }

    private async Task<InventoryReport> SerialsAsync(
        InventoryReportDefinition d, string asAt, bool money, CancellationToken ct)
    {
        var units = await db.StockUnits.Include(u => u.StockBatch).AsNoTracking()
            .OrderBy(u => u.Status).ThenBy(u => u.SerialNumber).Take(5000).ToListAsync(ct);
        var names = await ItemNamesAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);

        List<ReportColumn> columns =
        [
            new("Item", 3.5f), new("Serial", 2.5f), new("Status", 1.5f),
            new("Warehouse", 2), new("Batch", 1.5f), new("Received", 1.5f)
        ];
        if (money) columns.Add(new ReportColumn("Unit cost", 1.5f, true));

        return new InventoryReport(d.Name, asAt, columns,
            units.Select(u =>
            {
                var cells = new List<string>
                {
                    names.GetValueOrDefault((u.ItemType, u.ItemId), $"#{u.ItemId}"),
                    u.SerialNumber, u.Status.ToString(),
                    u.WarehouseId is { } w ? warehouses.GetValueOrDefault(w, "-") : "-",
                    u.StockBatch?.BatchNumber ?? "-",
                    u.ReceivedOn.ToString("yyyy-MM-dd")
                };
                if (money) cells.Add(u.UnitCost?.ToString("N2") ?? "-");
                return cells.ToArray();
            }).ToList());
    }

    private async Task<InventoryReport> BatchesAsync(
        InventoryReportDefinition d, string asAt, bool money, CancellationToken ct)
    {
        var batches = await db.StockBatches.AsNoTracking()
            .Where(b => b.RemainingQuantity > 0)
            .OrderBy(b => b.ExpiresOn ?? DateOnly.MaxValue).ThenBy(b => b.ReceivedOn)
            .ToListAsync(ct);
        var names = await ItemNamesAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        List<ReportColumn> columns =
        [
            new("Item", 3.5f), new("Batch", 2), new("Received", 1.5f),
            new("Expires", 1.5f), new("Received qty", 1.5f, true), new("Remaining", 1.5f, true),
            new("Status", 1.2f)
        ];
        if (money) columns.Add(new ReportColumn("Unit cost", 1.5f, true));

        return new InventoryReport(d.Name, asAt, columns,
            batches.Select(b =>
            {
                var cells = new List<string>
                {
                    names.GetValueOrDefault((b.ItemType, b.ItemId), $"#{b.ItemId}"),
                    b.BatchNumber, b.ReceivedOn.ToString("yyyy-MM-dd"),
                    b.ExpiresOn?.ToString("yyyy-MM-dd") ?? "-",
                    b.Quantity.ToString("0.##"), b.RemainingQuantity.ToString("0.##"),
                    b.IsExpired(today) ? "EXPIRED" : "OK"
                };
                if (money) cells.Add(b.UnitCost?.ToString("N2") ?? "-");
                return cells.ToArray();
            }).ToList());
    }

    private async Task<InventoryReport> MovementsAsync(
        InventoryReportDefinition d, string asAt, CancellationToken ct)
    {
        var moves = await db.StockTransactions.AsNoTracking()
            .OrderByDescending(t => t.Id).Take(2000).ToListAsync(ct);
        var names = await ItemNamesAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);

        return new InventoryReport(d.Name, asAt,
        [
            new("When", 1.8f), new("Item", 3.5f), new("Warehouse", 1.8f),
            new("Direction", 1.2f), new("Qty", 1.2f, true), new("Balance", 1.2f, true),
            new("Reason", 1.5f), new("Reference", 1.8f), new("By", 1.8f)
        ],
            moves.Select(m => new[]
            {
                m.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                names.GetValueOrDefault((m.ItemType, m.ItemId), $"#{m.ItemId}"),
                m.WarehouseId is { } w ? warehouses.GetValueOrDefault(w, "-") : "-",
                m.Direction.ToString(), m.Quantity.ToString("0.##"),
                m.BalanceAfter.ToString("0.##"), m.Reason.ToString(),
                m.Reference ?? "", m.PerformedByName
            }).ToList(),
            Note: "Most recent 2000 movements.");
    }

    private async Task<InventoryReport> OutstandingOrdersAsync(
        InventoryReportDefinition d, string asAt, CancellationToken ct)
    {
        var open = await orders.OutstandingAsync(ct);
        var rows = open.SelectMany(o => o.Lines.Where(l => l.Outstanding > 0)
            .Select(l => new[]
            {
                o.OrderNumber, o.Supplier?.Name ?? "-",
                o.ExpectedOn?.ToString("yyyy-MM-dd") ?? "-",
                l.ItemName, l.Quantity.ToString("0.##"),
                l.ReceivedQuantity.ToString("0.##"), l.Outstanding.ToString("0.##"),
                (l.Outstanding * l.UnitCost).ToString("N2")
            })).ToList();

        return new InventoryReport(d.Name, asAt,
        [
            new("Order", 1.6f), new("Supplier", 2.5f), new("Expected", 1.5f),
            new("Item", 3), new("Ordered", 1.2f, true), new("Received", 1.2f, true),
            new("Outstanding", 1.3f, true), new("Value", 1.5f, true)
        ],
            rows,
            ["", "", "", "", "", "", "Total",
                rows.Sum(r => decimal.Parse(r[7], System.Globalization.NumberStyles.Any)).ToString("N2")]);
    }

    /// <summary>
    /// Display names for every stock item, looked up once per report rather than
    /// per row — these reports join across thousands of movements.
    /// </summary>
    private async Task<Dictionary<(StockItemType, int), string>> ItemNamesAsync(CancellationToken ct)
    {
        var models = await db.ProductModels.Include(m => m.Product).AsNoTracking().ToListAsync(ct);
        var accessories = await db.Accessories.Include(a => a.ProductModel).AsNoTracking().ToListAsync(ct);

        var map = models.ToDictionary(
            m => (StockItemType.Model, m.Id), m => $"{m.Product.Name} › {m.Name}");
        foreach (var a in accessories)
            map[(StockItemType.Accessory, a.Id)] = $"{a.ProductModel.Name} › {a.Name}";
        return map;
    }
}
