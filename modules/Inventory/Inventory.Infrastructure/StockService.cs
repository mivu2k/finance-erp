using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

public record StockFilter(
    StockItemType? ItemType = null,
    int? ItemId = null,
    DateOnly? From = null,
    DateOnly? To = null);

public interface IStockService
{
    /// <summary>Adds stock in and appends the ledger row. Returns the new quantity.</summary>
    Task<decimal> AdjustAsync(StockItemType type, int itemId, StockDirection direction, decimal quantity,
        StockReason reason, string? reference, string? notes,
        string performedById, string performedByName, CancellationToken ct = default);

    Task<List<StockTransaction>> ListTransactionsAsync(StockFilter filter, CancellationToken ct = default);

    /// <summary>Rebuilds every cached quantity from the ledger, for drift recovery.</summary>
    Task RecalculateAsync(CancellationToken ct = default);

    Task<List<(StockItemType Type, int ItemId, string Name, decimal Quantity, decimal Threshold)>>
        ListLowStockAsync(CancellationToken ct = default);
}

public class StockService(InventoryDbContext db) : IStockService
{
    public async Task<decimal> AdjustAsync(StockItemType type, int itemId, StockDirection direction, decimal quantity,
        StockReason reason, string? reference, string? notes,
        string performedById, string performedByName, CancellationToken ct = default)
    {
        if (quantity <= 0)
            throw new InvalidOperationException("Quantity must be positive.");

        // EnableRetryOnFailure requires transactions to run through the execution
        // strategy so a retried attempt re-runs the whole unit, not a half-open one.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            decimal newQuantity;
            if (type == StockItemType.Model)
            {
                var model = await db.ProductModels.FirstOrDefaultAsync(m => m.Id == itemId, ct)
                    ?? throw new InvalidOperationException("Model not found.");
                newQuantity = direction == StockDirection.In ? model.CurrentQuantity + quantity : model.CurrentQuantity - quantity;
                if (newQuantity < 0)
                    throw new InvalidOperationException($"Only {model.CurrentQuantity} {model.Unit} of {model.Name} in stock.");
                model.CurrentQuantity = newQuantity;
            }
            else
            {
                var accessory = await db.Accessories.FirstOrDefaultAsync(a => a.Id == itemId, ct)
                    ?? throw new InvalidOperationException("Accessory not found.");
                newQuantity = direction == StockDirection.In ? accessory.CurrentQuantity + quantity : accessory.CurrentQuantity - quantity;
                if (newQuantity < 0)
                    throw new InvalidOperationException($"Only {accessory.CurrentQuantity} {accessory.Unit} of {accessory.Name} in stock.");
                accessory.CurrentQuantity = newQuantity;
            }

            db.StockTransactions.Add(new StockTransaction
            {
                ItemType = type,
                ItemId = itemId,
                Direction = direction,
                Quantity = quantity,
                Reason = reason,
                Reference = reference,
                Notes = notes,
                BalanceAfter = newQuantity,
                PerformedById = performedById,
                PerformedByName = performedByName
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return newQuantity;
        });
    }

    public async Task<List<StockTransaction>> ListTransactionsAsync(StockFilter filter, CancellationToken ct = default)
    {
        var q = db.StockTransactions.AsNoTracking().AsQueryable();

        if (filter.ItemType is { } t) q = q.Where(x => x.ItemType == t);
        if (filter.ItemId is { } id) q = q.Where(x => x.ItemId == id);
        if (filter.From is { } from) q = q.Where(x => x.CreatedAtUtc >= from.ToDateTime(TimeOnly.MinValue));
        if (filter.To is { } to) q = q.Where(x => x.CreatedAtUtc <= to.ToDateTime(TimeOnly.MaxValue));

        return await q.OrderByDescending(x => x.Id).Take(500).ToListAsync(ct);
    }

    public async Task RecalculateAsync(CancellationToken ct = default)
    {
        var models = await db.ProductModels.ToListAsync(ct);
        var accessories = await db.Accessories.ToListAsync(ct);
        var transactions = await db.StockTransactions.AsNoTracking().ToListAsync(ct);

        foreach (var model in models)
        {
            model.CurrentQuantity = transactions
                .Where(t => t.ItemType == StockItemType.Model && t.ItemId == model.Id)
                .Sum(t => t.Direction == StockDirection.In ? t.Quantity : -t.Quantity);
        }

        foreach (var accessory in accessories)
        {
            accessory.CurrentQuantity = transactions
                .Where(t => t.ItemType == StockItemType.Accessory && t.ItemId == accessory.Id)
                .Sum(t => t.Direction == StockDirection.In ? t.Quantity : -t.Quantity);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<(StockItemType Type, int ItemId, string Name, decimal Quantity, decimal Threshold)>>
        ListLowStockAsync(CancellationToken ct = default)
    {
        var models = await db.ProductModels.AsNoTracking()
            .Where(m => m.IsActive && m.CurrentQuantity <= m.ReorderThreshold)
            .Select(m => new ValueTuple<StockItemType, int, string, decimal, decimal>(
                StockItemType.Model, m.Id, m.Name, m.CurrentQuantity, m.ReorderThreshold))
            .ToListAsync(ct);

        var accessories = await db.Accessories.AsNoTracking()
            .Where(a => a.IsActive && a.CurrentQuantity <= a.ReorderThreshold)
            .Select(a => new ValueTuple<StockItemType, int, string, decimal, decimal>(
                StockItemType.Accessory, a.Id, a.Name, a.CurrentQuantity, a.ReorderThreshold))
            .ToListAsync(ct);

        return models.Concat(accessories)
            .Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5))
            .OrderBy(x => x.Item4)
            .ToList();
    }
}
