using ErpPlatform.Shared.Persistence;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

/// <summary>What one warehouse is holding.</summary>
public record WarehouseStockRow(
    StockItemType ItemType, int ItemId, string ItemName, string Unit, decimal Quantity);

public interface IWarehouseService
{
    Task<List<Warehouse>> ListAsync(bool activeOnly = false, CancellationToken ct = default);
    Task<Warehouse?> GetAsync(int id, CancellationToken ct = default);
    /// <summary>Where stock lands when nobody names a location.</summary>
    Task<Warehouse?> GetDefaultAsync(CancellationToken ct = default);
    Task<Warehouse> SaveAsync(Warehouse warehouse, CancellationToken ct = default);
    /// <summary>Soft-deletes a warehouse. Refused while it still holds stock.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task<List<WarehouseStockRow>> StockAtAsync(int warehouseId, CancellationToken ct = default);
}

public class WarehouseService(InventoryDbContext db) : IWarehouseService
{
    public async Task<List<Warehouse>> ListAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var q = db.Warehouses.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(w => w.IsActive);
        return await q.OrderByDescending(w => w.IsDefault).ThenBy(w => w.Name).ToListAsync(ct);
    }

    public Task<Warehouse?> GetAsync(int id, CancellationToken ct = default) =>
        db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

    public Task<Warehouse?> GetDefaultAsync(CancellationToken ct = default) =>
        db.Warehouses.AsNoTracking()
            .OrderByDescending(w => w.IsDefault).ThenBy(w => w.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<Warehouse> SaveAsync(Warehouse warehouse, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(warehouse.Name))
            throw new InvalidOperationException("Warehouse name is required.");

        if (warehouse.Id == 0)
        {
            // The first one is automatically the default, or nothing would have a home.
            if (!await db.Warehouses.AnyAsync(ct)) warehouse.IsDefault = true;
            db.Warehouses.Add(warehouse);
        }
        else
        {
            var existing = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == warehouse.Id, ct)
                ?? throw new InvalidOperationException("Warehouse not found.");

            existing.Name = warehouse.Name;
            existing.Code = warehouse.Code;
            existing.Address = warehouse.Address;
            existing.Notes = warehouse.Notes;
            existing.IsActive = warehouse.IsActive;
            existing.IsDefault = warehouse.IsDefault;
            warehouse = existing;
        }

        await db.SaveChangesAsync(ct);

        // Exactly one default, so an unnamed movement is never ambiguous.
        if (warehouse.IsDefault)
        {
            var others = await db.Warehouses
                .Where(w => w.Id != warehouse.Id && w.IsDefault).ToListAsync(ct);
            if (others.Count > 0)
            {
                foreach (var other in others) other.IsDefault = false;
                await db.SaveChangesAsync(ct);
            }
        }

        return warehouse;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (warehouse is null) return;

        var held = await db.StockBalances
            .Where(b => b.WarehouseId == id && b.Quantity != 0).SumAsync(b => b.Quantity, ct);
        if (held != 0)
            throw new InvalidOperationException(
                $"{warehouse.Name} still holds {held:0.##} in stock. Move it out first.");

        if (warehouse.IsDefault && await db.Warehouses.CountAsync(ct) > 1)
            throw new InvalidOperationException(
                "The default warehouse can't be deleted. Make another one the default first.");

        db.Warehouses.Remove(warehouse);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<WarehouseStockRow>> StockAtAsync(
        int warehouseId, CancellationToken ct = default)
    {
        var balances = await db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == warehouseId && b.Quantity != 0).ToListAsync(ct);

        var modelIds = balances.Where(b => b.ItemType == StockItemType.Model)
            .Select(b => b.ItemId).ToList();
        var accessoryIds = balances.Where(b => b.ItemType == StockItemType.Accessory)
            .Select(b => b.ItemId).ToList();

        var models = await db.ProductModels.Include(m => m.Product).AsNoTracking()
            .Where(m => modelIds.Contains(m.Id)).ToListAsync(ct);
        var accessories = await db.Accessories.Include(a => a.ProductModel).AsNoTracking()
            .Where(a => accessoryIds.Contains(a.Id)).ToListAsync(ct);

        return balances.Select(b =>
        {
            if (b.ItemType == StockItemType.Model)
            {
                var m = models.FirstOrDefault(x => x.Id == b.ItemId);
                return new WarehouseStockRow(b.ItemType, b.ItemId,
                    m is null ? $"#{b.ItemId}" : $"{m.Product.Name} › {m.Name}",
                    m?.Unit ?? "", b.Quantity);
            }

            var a = accessories.FirstOrDefault(x => x.Id == b.ItemId);
            return new WarehouseStockRow(b.ItemType, b.ItemId,
                a is null ? $"#{b.ItemId}" : $"{a.ProductModel.Name} › {a.Name}",
                a?.Unit ?? "", b.Quantity);
        }).OrderBy(r => r.ItemName).ToList();
    }
}

public interface IStockTransferService
{
    Task<List<StockTransfer>> ListAsync(StockTransferStatus? status = null, CancellationToken ct = default);
    Task<StockTransfer?> GetAsync(int id, CancellationToken ct = default);
    Task<StockTransfer> CreateAsync(StockTransfer transfer, string userId, string userName,
        CancellationToken ct = default);
    Task<StockTransfer> UpdateAsync(StockTransfer transfer, CancellationToken ct = default);

    /// <summary>Takes the goods out of the source. They are now in transit, in neither place.</summary>
    Task<StockTransfer> DispatchAsync(int id, string userId, string userName, string? byName,
        CancellationToken ct = default);

    /// <summary>
    /// Books what actually arrived into the destination. Receiving short is allowed
    /// and recorded — that gap is the whole reason for having an in-transit state.
    /// </summary>
    Task<StockTransfer> ReceiveAsync(int id, IReadOnlyDictionary<int, decimal> receivedByLineId,
        string userId, string userName, string? byName, CancellationToken ct = default);

    Task<StockTransfer> CancelAsync(int id, CancellationToken ct = default);
}

public class StockTransferService(InventoryDbContext db, IStockService stock) : IStockTransferService
{
    public async Task<List<StockTransfer>> ListAsync(
        StockTransferStatus? status = null, CancellationToken ct = default)
    {
        var q = db.StockTransfers
            .Include(t => t.FromWarehouse).Include(t => t.ToWarehouse).Include(t => t.Lines)
            .AsNoTracking().AsSplitQuery().AsQueryable();
        if (status is { } s) q = q.Where(t => t.Status == s);
        return await q.OrderByDescending(t => t.Id).Take(300).ToListAsync(ct);
    }

    public Task<StockTransfer?> GetAsync(int id, CancellationToken ct = default) =>
        db.StockTransfers
            .Include(t => t.FromWarehouse).Include(t => t.ToWarehouse).Include(t => t.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<StockTransfer> CreateAsync(
        StockTransfer transfer, string userId, string userName, CancellationToken ct = default)
    {
        Validate(transfer);

        transfer.TransferNumber = await new DocumentNumberService(db).NextAsync("StockTransfer", "TRF", ct);
        transfer.Status = StockTransferStatus.Draft;
        transfer.RaisedById = userId;
        transfer.RaisedByName = userName;
        if (transfer.Date == default) transfer.Date = DateOnly.FromDateTime(DateTime.UtcNow);

        db.StockTransfers.Add(transfer);
        await db.SaveChangesAsync(ct);
        return transfer;
    }

    public async Task<StockTransfer> UpdateAsync(StockTransfer transfer, CancellationToken ct = default)
    {
        var existing = await Require(transfer.Id, ct);
        if (existing.Status != StockTransferStatus.Draft)
            throw new InvalidOperationException("Only a draft transfer can be edited.");

        Validate(transfer);

        existing.Date = transfer.Date;
        existing.FromWarehouseId = transfer.FromWarehouseId;
        existing.ToWarehouseId = transfer.ToWarehouseId;
        existing.Reference = transfer.Reference;
        existing.Notes = transfer.Notes;

        db.StockTransferLines.RemoveRange(existing.Lines);
        existing.Lines = transfer.Lines.Select(l => new StockTransferLine
        {
            ItemType = l.ItemType, ItemId = l.ItemId, ItemName = l.ItemName,
            Quantity = l.Quantity, SerialNumbers = l.SerialNumbers, Note = l.Note
        }).ToList();

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<StockTransfer> DispatchAsync(
        int id, string userId, string userName, string? byName, CancellationToken ct = default)
    {
        var transfer = await Require(id, ct);
        if (transfer.Status != StockTransferStatus.Draft)
            throw new InvalidOperationException("Only a draft transfer can be dispatched.");
        if (transfer.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one line.");

        foreach (var line in transfer.Lines)
            await stock.AdjustAsync(line.ItemType, line.ItemId, StockDirection.Out, line.Quantity,
                StockReason.Adjustment, transfer.TransferNumber,
                $"Transfer out to {transfer.ToWarehouse.Name}",
                userId, userName, transfer.FromWarehouseId, ct);

        transfer.Status = StockTransferStatus.InTransit;
        transfer.DispatchedAtUtc = DateTime.UtcNow;
        transfer.DispatchedByName = byName ?? userName;
        await db.SaveChangesAsync(ct);
        return transfer;
    }

    public async Task<StockTransfer> ReceiveAsync(
        int id, IReadOnlyDictionary<int, decimal> receivedByLineId,
        string userId, string userName, string? byName, CancellationToken ct = default)
    {
        var transfer = await Require(id, ct);
        if (transfer.Status != StockTransferStatus.InTransit)
            throw new InvalidOperationException("Only a dispatched transfer can be received.");

        foreach (var line in transfer.Lines)
        {
            // A line nobody touched is taken as fully arrived; that is the common case
            // and forcing a keystroke per line to say "yes, all of it" helps nobody.
            var received = receivedByLineId.TryGetValue(line.Id, out var r) ? r : line.Quantity;
            if (received < 0)
                throw new InvalidOperationException($"{line.ItemName}: received can't be negative.");
            if (received > line.Quantity)
                throw new InvalidOperationException(
                    $"{line.ItemName}: more arrived than was sent. Raise a separate receipt for the extra.");

            line.ReceivedQuantity = received;

            if (received > 0)
                await stock.AdjustAsync(line.ItemType, line.ItemId, StockDirection.In, received,
                    StockReason.Adjustment, transfer.TransferNumber,
                    $"Transfer in from {transfer.FromWarehouse.Name}",
                    userId, userName, transfer.ToWarehouseId, ct);
        }

        transfer.Status = StockTransferStatus.Received;
        transfer.ReceivedAtUtc = DateTime.UtcNow;
        transfer.ReceivedByName = byName ?? userName;
        await db.SaveChangesAsync(ct);
        return transfer;
    }

    public async Task<StockTransfer> CancelAsync(int id, CancellationToken ct = default)
    {
        var transfer = await Require(id, ct);
        if (transfer.Status == StockTransferStatus.Received)
            throw new InvalidOperationException("A received transfer can't be cancelled.");
        if (transfer.Status == StockTransferStatus.InTransit)
            throw new InvalidOperationException(
                "These goods have already left the source. Receive the transfer — short if " +
                "necessary — so the stock lands somewhere rather than disappearing.");

        transfer.Status = StockTransferStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return transfer;
    }

    private static void Validate(StockTransfer transfer)
    {
        if (transfer.FromWarehouseId == 0 || transfer.ToWarehouseId == 0)
            throw new InvalidOperationException("Pick both a source and a destination.");
        if (transfer.FromWarehouseId == transfer.ToWarehouseId)
            throw new InvalidOperationException("Source and destination must be different.");
        if (transfer.Lines.Any(l => l.Quantity <= 0))
            throw new InvalidOperationException("Line quantities must be positive.");
    }

    private async Task<StockTransfer> Require(int id, CancellationToken ct) =>
        await db.StockTransfers
            .Include(t => t.FromWarehouse).Include(t => t.ToWarehouse).Include(t => t.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(t => t.Id == id, ct)
        ?? throw new InvalidOperationException("Transfer not found.");
}
