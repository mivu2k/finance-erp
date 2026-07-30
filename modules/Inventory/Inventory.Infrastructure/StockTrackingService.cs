using ErpPlatform.Shared.Persistence;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

/// <summary>One line of a stock valuation.</summary>
public record ValuationRow(
    StockItemType ItemType, int ItemId, string ProductName, string ItemName, string Unit,
    decimal Quantity, decimal? AverageCost, decimal Value, decimal SalePrice,
    decimal ReorderThreshold, bool IsLow);

/// <summary>What arrives in one receipt.</summary>
/// <param name="SerialNumbers">
/// Required for a serialised item, and its count must match the quantity — otherwise
/// the units on the shelf and the number in the ledger start disagreeing on day one.
/// </param>
public record StockReceipt(
    StockItemType ItemType,
    int ItemId,
    decimal Quantity,
    decimal? UnitCost = null,
    string? BatchNumber = null,
    DateOnly? ExpiresOn = null,
    IReadOnlyList<string>? SerialNumbers = null,
    string? Reference = null,
    string? Notes = null);

public interface IStockTrackingService
{
    /// <summary>
    /// Takes goods in: moves the quantity, re-derives the average cost, opens a batch
    /// where the item is batch-tracked, and creates one unit per serial where it is
    /// serialised.
    /// </summary>
    Task ReceiveAsync(StockReceipt receipt, string userId, string userName,
        CancellationToken ct = default);

    /// <summary>Issues named serialised units out of stock.</summary>
    Task IssueSerialsAsync(StockItemType type, int itemId, IReadOnlyList<string> serials,
        StockUnitStatus status, string? issuedTo, string? reference,
        string userId, string userName, CancellationToken ct = default);

    Task<List<StockUnit>> ListUnitsAsync(StockItemType type, int itemId,
        StockUnitStatus? status = null, CancellationToken ct = default);
    /// <summary>Finds a unit anywhere by its serial — what a scanner needs.</summary>
    Task<StockUnit?> FindBySerialAsync(string serial, CancellationToken ct = default);
    Task<StockUnit> UpdateUnitAsync(StockUnit unit, CancellationToken ct = default);

    Task<List<StockBatch>> ListBatchesAsync(StockItemType type, int itemId,
        bool openOnly = false, CancellationToken ct = default);
    Task<List<StockBatch>> ListExpiringAsync(int withinDays = 30, CancellationToken ct = default);

    Task<List<ValuationRow>> ValuationAsync(CancellationToken ct = default);
}

public class StockTrackingService(InventoryDbContext db, IStockService stock) : IStockTrackingService
{
    public async Task ReceiveAsync(
        StockReceipt receipt, string userId, string userName, CancellationToken ct = default)
    {
        if (receipt.Quantity <= 0)
            throw new InvalidOperationException("Quantity must be positive.");
        if (receipt.UnitCost is < 0)
            throw new InvalidOperationException("Unit cost can't be negative.");

        var (name, isSerialised, isBatched) = await DescribeAsync(receipt.ItemType, receipt.ItemId, ct);

        var serials = receipt.SerialNumbers ?? [];
        if (isSerialised)
        {
            if (serials.Count != (int)receipt.Quantity)
                throw new InvalidOperationException(
                    $"{name} is serialised: give exactly {receipt.Quantity:0.##} serial number(s), " +
                    $"not {serials.Count}.");
            if (serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Count)
                throw new InvalidOperationException("The same serial appears twice in this receipt.");

            var clash = await db.StockUnits.AsNoTracking()
                .Where(u => u.ItemType == receipt.ItemType && u.ItemId == receipt.ItemId
                            && serials.Contains(u.SerialNumber))
                .Select(u => u.SerialNumber).FirstOrDefaultAsync(ct);
            if (clash is not null)
                throw new InvalidOperationException($"Serial {clash} is already on record for {name}.");
        }
        else if (serials.Count > 0)
        {
            throw new InvalidOperationException($"{name} isn't serialised, so it takes no serial numbers.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        StockBatch? batch = null;
        if (isBatched)
        {
            if (string.IsNullOrWhiteSpace(receipt.BatchNumber))
                throw new InvalidOperationException($"{name} is batch-tracked, so a batch number is required.");

            batch = new StockBatch
            {
                ItemType = receipt.ItemType, ItemId = receipt.ItemId,
                BatchNumber = receipt.BatchNumber.Trim(),
                ReceivedOn = today, ExpiresOn = receipt.ExpiresOn,
                Quantity = receipt.Quantity, RemainingQuantity = receipt.Quantity,
                UnitCost = receipt.UnitCost, Reference = receipt.Reference
            };
            db.StockBatches.Add(batch);
            await db.SaveChangesAsync(ct);
        }

        foreach (var serial in serials)
            db.StockUnits.Add(new StockUnit
            {
                ItemType = receipt.ItemType, ItemId = receipt.ItemId,
                SerialNumber = serial.Trim(), Status = StockUnitStatus.InStock,
                StockBatchId = batch?.Id, UnitCost = receipt.UnitCost,
                ReceivedOn = today, Reference = receipt.Reference
            });

        if (serials.Count > 0) await db.SaveChangesAsync(ct);

        // The quantity still moves through the ordinary ledger, so a receipt is as
        // traceable as any other movement and the balance stays rebuildable.
        await stock.AdjustAsync(receipt.ItemType, receipt.ItemId, StockDirection.In,
            receipt.Quantity, StockReason.Purchase, receipt.Reference, receipt.Notes,
            userId, userName, ct: ct);

        await ApplyCostAsync(receipt.ItemType, receipt.ItemId, receipt.Quantity, receipt.UnitCost, ct);
    }

    public async Task IssueSerialsAsync(
        StockItemType type, int itemId, IReadOnlyList<string> serials, StockUnitStatus status,
        string? issuedTo, string? reference, string userId, string userName,
        CancellationToken ct = default)
    {
        if (serials.Count == 0)
            throw new InvalidOperationException("Pick at least one serial to issue.");
        if (status is StockUnitStatus.InStock or StockUnitStatus.Returned)
            throw new InvalidOperationException("Issuing has to move a unit out of stock.");

        var units = await db.StockUnits
            .Where(u => u.ItemType == type && u.ItemId == itemId && serials.Contains(u.SerialNumber))
            .ToListAsync(ct);

        var missing = serials.Except(units.Select(u => u.SerialNumber), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Not on record: {string.Join(", ", missing)}.");

        var notInStock = units.Where(u => !u.CountsAsStock).ToList();
        if (notInStock.Count > 0)
            throw new InvalidOperationException(
                $"Already out of stock: {string.Join(", ", notInStock.Select(u => $"{u.SerialNumber} ({u.Status})"))}.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var unit in units)
        {
            unit.Status = status;
            unit.IssuedOn = today;
            unit.IssuedTo = issuedTo;
            unit.Reference = reference ?? unit.Reference;

            // Free the batch back up so a batch's remaining figure follows its units.
            if (unit.StockBatchId is { } batchId)
            {
                var batch = await db.StockBatches.FirstOrDefaultAsync(x => x.Id == batchId, ct);
                if (batch is not null)
                    batch.RemainingQuantity = Math.Max(0, batch.RemainingQuantity - 1);
            }
        }
        await db.SaveChangesAsync(ct);

        await stock.AdjustAsync(type, itemId, StockDirection.Out, units.Count,
            status == StockUnitStatus.Sold ? StockReason.Sale : StockReason.Adjustment,
            reference, $"Serials: {string.Join(", ", serials)}", userId, userName, ct: ct);
    }

    public async Task<List<StockUnit>> ListUnitsAsync(
        StockItemType type, int itemId, StockUnitStatus? status = null, CancellationToken ct = default)
    {
        var q = db.StockUnits.Include(u => u.StockBatch).AsNoTracking()
            .Where(u => u.ItemType == type && u.ItemId == itemId);
        if (status is { } s) q = q.Where(u => u.Status == s);
        return await q.OrderBy(u => u.SerialNumber).ToListAsync(ct);
    }

    public Task<StockUnit?> FindBySerialAsync(string serial, CancellationToken ct = default) =>
        db.StockUnits.Include(u => u.StockBatch).AsNoTracking()
            .FirstOrDefaultAsync(u => u.SerialNumber == serial, ct);

    public async Task<StockUnit> UpdateUnitAsync(StockUnit unit, CancellationToken ct = default)
    {
        var existing = await db.StockUnits.FirstOrDefaultAsync(u => u.Id == unit.Id, ct)
            ?? throw new InvalidOperationException("Unit not found.");

        existing.Status = unit.Status;
        existing.IssuedTo = unit.IssuedTo;
        existing.Reference = unit.Reference;
        existing.Notes = unit.Notes;
        existing.UnitCost = unit.UnitCost;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<List<StockBatch>> ListBatchesAsync(
        StockItemType type, int itemId, bool openOnly = false, CancellationToken ct = default)
    {
        var q = db.StockBatches.AsNoTracking().Where(b => b.ItemType == type && b.ItemId == itemId);
        if (openOnly) q = q.Where(b => b.RemainingQuantity > 0);
        return await q.OrderBy(b => b.ReceivedOn).ToListAsync(ct);
    }

    public async Task<List<StockBatch>> ListExpiringAsync(
        int withinDays = 30, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(withinDays));
        return await db.StockBatches.AsNoTracking()
            .Where(b => b.RemainingQuantity > 0 && b.ExpiresOn != null && b.ExpiresOn <= cutoff)
            .OrderBy(b => b.ExpiresOn).ToListAsync(ct);
    }

    public async Task<List<ValuationRow>> ValuationAsync(CancellationToken ct = default)
    {
        var models = await db.ProductModels.Include(m => m.Product).AsNoTracking().ToListAsync(ct);
        var accessories = await db.Accessories
            .Include(a => a.ProductModel).ThenInclude(m => m.Product)
            .AsNoTracking().ToListAsync(ct);

        var rows = models.Select(m => new ValuationRow(
                StockItemType.Model, m.Id, m.Product.Name, m.Name, m.Unit,
                m.CurrentQuantity, m.AverageCost, m.StockValue, m.SalePrice,
                m.ReorderThreshold, m.CurrentQuantity <= m.ReorderThreshold))
            .Concat(accessories.Select(a => new ValuationRow(
                StockItemType.Accessory, a.Id, a.ProductModel.Product.Name,
                $"{a.ProductModel.Name} › {a.Name}", a.Unit,
                a.CurrentQuantity, a.AverageCost, a.StockValue, a.SalePrice,
                a.ReorderThreshold, a.CurrentQuantity <= a.ReorderThreshold)));

        return rows.OrderBy(r => r.ProductName).ThenBy(r => r.ItemName).ToList();
    }

    // --- helpers ---

    private async Task<(string Name, bool Serialised, bool Batched)> DescribeAsync(
        StockItemType type, int itemId, CancellationToken ct)
    {
        if (type == StockItemType.Model)
        {
            var m = await db.ProductModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, ct)
                ?? throw new InvalidOperationException("Model not found.");
            return (m.Name, m.IsSerialised, m.IsBatchTracked);
        }

        var a = await db.Accessories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, ct)
            ?? throw new InvalidOperationException("Accessory not found.");
        return (a.Name, a.IsSerialised, a.IsBatchTracked);
    }

    /// <summary>
    /// Rolls a receipt into the item's cost figures. The average is quantity weighted
    /// and accumulated; "last" only moves when the receipt is genuinely the newest, so
    /// an old invoice entered late updates the average without overwriting a newer cost.
    /// </summary>
    private async Task ApplyCostAsync(
        StockItemType type, int itemId, decimal quantity, decimal? unitCost, CancellationToken ct)
    {
        if (unitCost is not { } cost) return;

        if (type == StockItemType.Model)
        {
            var m = await db.ProductModels.FirstAsync(x => x.Id == itemId, ct);
            (m.PurchasedQuantity, m.AverageCost, m.LastPurchaseCost) =
                Blend(m.PurchasedQuantity, m.AverageCost, quantity, cost);
        }
        else
        {
            var a = await db.Accessories.FirstAsync(x => x.Id == itemId, ct);
            (a.PurchasedQuantity, a.AverageCost, a.LastPurchaseCost) =
                Blend(a.PurchasedQuantity, a.AverageCost, quantity, cost);
        }

        await db.SaveChangesAsync(ct);
        return;

        static (decimal Purchased, decimal? Average, decimal Last) Blend(
            decimal purchased, decimal? currentAverage, decimal qty, decimal cost)
        {
            var priorValue = (currentAverage ?? 0) * purchased;
            var total = purchased + qty;
            var average = total > 0 ? Math.Round((priorValue + qty * cost) / total, 4) : cost;
            return (total, average, cost);
        }
    }
}
