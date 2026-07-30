using ErpPlatform.Shared.Persistence;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

public interface IStockCountService
{
    Task<List<StockCount>> ListAsync(StockCountStatus? status = null, CancellationToken ct = default);
    Task<StockCount?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Opens a count and takes a snapshot of what the system currently holds, so the
    /// sheet a counter works from is fixed even if stock moves while they walk around.
    /// </summary>
    Task<StockCount> StartAsync(string? notes, string userId, string userName,
        CancellationToken ct = default);

    /// <summary>Records what was actually found. Null leaves a line uncounted.</summary>
    Task SaveCountsAsync(int countId, IReadOnlyDictionary<int, decimal?> countedByLineId,
        CancellationToken ct = default);

    Task<StockCount> MarkCountedAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Writes every variance to the stock ledger as an adjustment, then locks the
    /// count. Uncounted lines are left alone — a blank is "not looked at", not "zero".
    /// </summary>
    Task<StockCount> PostAsync(int id, string userId, string userName, CancellationToken ct = default);

    Task<StockCount> CancelAsync(int id, CancellationToken ct = default);
}

public class StockCountService(InventoryDbContext db, IStockService stock) : IStockCountService
{
    public async Task<List<StockCount>> ListAsync(
        StockCountStatus? status = null, CancellationToken ct = default)
    {
        var q = db.StockCounts.Include(c => c.Lines).AsNoTracking().AsSplitQuery().AsQueryable();
        if (status is { } s) q = q.Where(c => c.Status == s);
        return await q.OrderByDescending(c => c.Id).Take(200).ToListAsync(ct);
    }

    public Task<StockCount?> GetAsync(int id, CancellationToken ct = default) =>
        db.StockCounts.Include(c => c.Lines).AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<StockCount> StartAsync(
        string? notes, string userId, string userName, CancellationToken ct = default)
    {
        if (await db.StockCounts.AnyAsync(c => c.Status == StockCountStatus.Draft, ct))
            throw new InvalidOperationException(
                "A count is already open. Finish or cancel it before starting another.");

        var count = new StockCount
        {
            CountNumber = await new DocumentNumberService(db).NextAsync("StockCount", "SC", ct),
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = StockCountStatus.Draft,
            Notes = notes,
            CountedById = userId,
            CountedByName = userName
        };

        var models = await db.ProductModels.Include(m => m.Product).AsNoTracking()
            .Where(m => m.IsActive).ToListAsync(ct);
        var accessories = await db.Accessories
            .Include(a => a.ProductModel).AsNoTracking()
            .Where(a => a.IsActive).ToListAsync(ct);

        count.Lines =
        [
            .. models.Select(m => new StockCountLine
            {
                ItemType = StockItemType.Model, ItemId = m.Id,
                ItemName = $"{m.Product.Name} › {m.Name}",
                SystemQuantity = m.CurrentQuantity
            }),
            .. accessories.Select(a => new StockCountLine
            {
                ItemType = StockItemType.Accessory, ItemId = a.Id,
                ItemName = $"{a.ProductModel.Name} › {a.Name}",
                SystemQuantity = a.CurrentQuantity
            })
        ];

        if (count.Lines.Count == 0)
            throw new InvalidOperationException("There is nothing active to count.");

        db.StockCounts.Add(count);
        await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task SaveCountsAsync(
        int countId, IReadOnlyDictionary<int, decimal?> countedByLineId, CancellationToken ct = default)
    {
        var count = await Require(countId, ct);
        if (count.Status is StockCountStatus.Posted or StockCountStatus.Cancelled)
            throw new InvalidOperationException("This count is closed.");

        foreach (var line in count.Lines)
        {
            if (!countedByLineId.TryGetValue(line.Id, out var counted)) continue;
            if (counted is < 0)
                throw new InvalidOperationException($"{line.ItemName}: a count can't be negative.");
            line.CountedQuantity = counted;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<StockCount> MarkCountedAsync(int id, CancellationToken ct = default)
    {
        var count = await Require(id, ct);
        if (count.Status != StockCountStatus.Draft)
            throw new InvalidOperationException("Only an open count can be finished.");
        if (count.Lines.All(l => l.CountedQuantity is null))
            throw new InvalidOperationException("Nothing has been counted yet.");

        count.Status = StockCountStatus.Counted;
        await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task<StockCount> PostAsync(
        int id, string userId, string userName, CancellationToken ct = default)
    {
        var count = await Require(id, ct);
        if (count.Status != StockCountStatus.Counted)
            throw new InvalidOperationException("Finish counting before posting.");

        // Variances go through the ordinary ledger rather than writing quantities
        // directly, so a correction is as traceable as any other movement.
        foreach (var line in count.Lines.Where(l => l.CountedQuantity is not null && l.Variance != 0))
        {
            var variance = line.Variance;
            await stock.AdjustAsync(
                line.ItemType, line.ItemId,
                variance > 0 ? StockDirection.In : StockDirection.Out,
                Math.Abs(variance), StockReason.Adjustment,
                count.CountNumber,
                $"Stock take {count.CountNumber}: counted {line.CountedQuantity:0.##} " +
                $"against {line.SystemQuantity:0.##}",
                userId, userName, ct);
        }

        count.Status = StockCountStatus.Posted;
        count.PostedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task<StockCount> CancelAsync(int id, CancellationToken ct = default)
    {
        var count = await Require(id, ct);
        if (count.Status == StockCountStatus.Posted)
            throw new InvalidOperationException("A posted count can't be cancelled — its stock has moved.");

        count.Status = StockCountStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return count;
    }

    private async Task<StockCount> Require(int id, CancellationToken ct) =>
        await db.StockCounts.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new InvalidOperationException("Stock count not found.");
}
