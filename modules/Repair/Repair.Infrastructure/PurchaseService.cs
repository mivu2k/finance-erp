using ErpPlatform.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

public record PurchaseFilter(
    string? Search = null,
    int? SupplierId = null,
    int? PartId = null,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>One part's price history, for the cost trend report.</summary>
public record PriceHistoryPoint(
    DateOnly PurchasedOn,
    string PurchaseNumber,
    string SupplierName,
    decimal Quantity,
    decimal UnitCost);

public interface IPurchaseService
{
    Task<List<Supplier>> ListSuppliersAsync(string? search = null, CancellationToken ct = default);
    Task<Supplier> SaveSupplierAsync(Supplier supplier, CancellationToken ct = default);

    Task<List<PartPurchase>> ListAsync(PurchaseFilter filter, CancellationToken ct = default);
    Task<PartPurchase?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Records a purchase and re-derives each part's cost from it. This is the only
    /// way a part's cost changes.
    /// </summary>
    Task<PartPurchase> ReceiveAsync(PartPurchase purchase, string userId, string userName,
        CancellationToken ct = default);

    Task<List<PriceHistoryPoint>> GetPriceHistoryAsync(int partId, CancellationToken ct = default);

    /// <summary>Recomputes every part's cost figures from the purchase ledger.</summary>
    Task<int> RecalculateCostsAsync(CancellationToken ct = default);

    static void Recalculate(PartPurchase p)
    {
        foreach (var item in p.Items)
            item.LineTotal = Math.Round(item.Quantity * item.UnitCost, 2);

        p.Subtotal = p.Items.Sum(i => i.LineTotal);
        p.TotalAmount = Math.Round(
            p.Subtotal - p.DiscountAmount + p.TaxAmount + p.OtherCharges, 2);
    }
}

public class PurchaseService(RepairDbContext db) : IPurchaseService
{
    public Task<List<Supplier>> ListSuppliersAsync(
        string? search = null, CancellationToken ct = default)
    {
        var q = db.Suppliers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.Name.Contains(s)
                          || (x.Phone != null && x.Phone.Contains(s)));
        }

        return q.OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<Supplier> SaveSupplierAsync(Supplier supplier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(supplier.Name))
            throw new InvalidOperationException("Supplier name is required.");

        if (supplier.Id == 0) db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);
        return supplier;
    }

    public Task<List<PartPurchase>> ListAsync(PurchaseFilter filter, CancellationToken ct = default)
    {
        var q = db.PartPurchases
            .Include(p => p.Supplier)
            .Include(p => p.Items)
            .AsNoTracking().AsQueryable();

        if (filter.SupplierId is { } supplierId) q = q.Where(p => p.SupplierId == supplierId);
        if (filter.PartId is { } partId) q = q.Where(p => p.Items.Any(i => i.PartId == partId));
        if (filter.From is { } from) q = q.Where(p => p.PurchasedOn >= from);
        if (filter.To is { } to) q = q.Where(p => p.PurchasedOn <= to);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(p => p.PurchaseNumber.Contains(s)
                          || p.Supplier.Name.Contains(s)
                          || (p.SupplierInvoiceNumber != null && p.SupplierInvoiceNumber.Contains(s)));
        }

        return q.OrderByDescending(p => p.Id).Take(500).ToListAsync(ct);
    }

    public Task<PartPurchase?> GetAsync(int id, CancellationToken ct = default) =>
        db.PartPurchases
            .Include(p => p.Supplier)
            .Include(p => p.Items).ThenInclude(i => i.Part)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<PartPurchase> ReceiveAsync(
        PartPurchase purchase, string userId, string userName, CancellationToken ct = default)
    {
        if (purchase.SupplierId == 0)
            throw new InvalidOperationException("Select a supplier.");
        if (purchase.Items.Count == 0)
            throw new InvalidOperationException("Add at least one part.");
        if (purchase.Items.Any(i => i.PartId == 0))
            throw new InvalidOperationException("Every line must name a part.");
        if (purchase.Items.Any(i => i.Quantity <= 0))
            throw new InvalidOperationException("Quantities must be positive.");
        if (purchase.Items.Any(i => i.UnitCost < 0))
            throw new InvalidOperationException("Unit cost can't be negative.");

        IPurchaseService.Recalculate(purchase);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        purchase.PurchaseNumber = await new DocumentNumberService(db)
            .NextAsync("PartPurchase", "PUR", ct);
        purchase.ReceivedById = userId;
        purchase.ReceivedByName = userName;
        if (purchase.PurchasedOn == default)
            purchase.PurchasedOn = DateOnly.FromDateTime(DateTime.Today);

        db.PartPurchases.Add(purchase);
        await db.SaveChangesAsync(ct);

        await ApplyToPartsAsync(purchase, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return purchase;
    }

    /// <summary>
    /// Rolls a receipt into each part's cost figures. The average is quantity
    /// weighted and accumulated, so buying 100 at 50 then 1 at 500 doesn't drag the
    /// average to 275 the way a simple mean would.
    /// </summary>
    private async Task ApplyToPartsAsync(PartPurchase purchase, CancellationToken ct)
    {
        var partIds = purchase.Items.Select(i => i.PartId).Distinct().ToList();
        var parts = await db.Parts.Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        foreach (var group in purchase.Items.GroupBy(i => i.PartId))
        {
            if (!parts.TryGetValue(group.Key, out var part)) continue;

            var quantity = group.Sum(i => i.Quantity);
            var value = group.Sum(i => i.LineTotal);
            if (quantity <= 0) continue;

            var priorQuantity = part.PurchasedQuantity;
            var priorValue = (part.AverageCost ?? 0) * priorQuantity;

            part.PurchasedQuantity = priorQuantity + quantity;
            part.AverageCost = Math.Round((priorValue + value) / part.PurchasedQuantity, 4);

            // "Last" means the newest purchase, not necessarily this one — an
            // older invoice entered late must not overwrite a newer cost.
            if (part.LastPurchasedOn is null || purchase.PurchasedOn >= part.LastPurchasedOn)
            {
                part.LastPurchaseCost = Math.Round(value / quantity, 4);
                part.LastPurchasedOn = purchase.PurchasedOn;
                part.LastSupplierId = purchase.SupplierId;
            }

            var reprice = group.LastOrDefault(i => i.NewSellingPrice is > 0)?.NewSellingPrice;
            if (reprice is { } price) part.Price = price;
        }
    }

    public async Task<List<PriceHistoryPoint>> GetPriceHistoryAsync(
        int partId, CancellationToken ct = default)
    {
        var rows = await db.PartPurchaseItems
            .Include(i => i.PartPurchase).ThenInclude(p => p.Supplier)
            .Where(i => i.PartId == partId)
            .OrderBy(i => i.PartPurchase.PurchasedOn)
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.Select(i => new PriceHistoryPoint(
            i.PartPurchase.PurchasedOn,
            i.PartPurchase.PurchaseNumber,
            i.PartPurchase.Supplier.Name,
            i.Quantity,
            i.Quantity > 0 ? Math.Round(i.LineTotal / i.Quantity, 4) : i.UnitCost)).ToList();
    }

    public async Task<int> RecalculateCostsAsync(CancellationToken ct = default)
    {
        var parts = await db.Parts.ToListAsync(ct);
        var items = await db.PartPurchaseItems
            .Include(i => i.PartPurchase)
            .AsNoTracking()
            .ToListAsync(ct);

        var byPart = items.GroupBy(i => i.PartId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var part in parts)
        {
            if (!byPart.TryGetValue(part.Id, out var lines) || lines.Count == 0)
            {
                part.LastPurchaseCost = null;
                part.LastPurchasedOn = null;
                part.LastSupplierId = null;
                part.AverageCost = null;
                part.PurchasedQuantity = 0;
                continue;
            }

            var quantity = lines.Sum(l => l.Quantity);
            part.PurchasedQuantity = quantity;
            part.AverageCost = quantity > 0
                ? Math.Round(lines.Sum(l => l.LineTotal) / quantity, 4)
                : null;

            var newest = lines.OrderByDescending(l => l.PartPurchase.PurchasedOn)
                .ThenByDescending(l => l.Id).First();
            part.LastPurchaseCost = newest.Quantity > 0
                ? Math.Round(newest.LineTotal / newest.Quantity, 4)
                : newest.UnitCost;
            part.LastPurchasedOn = newest.PartPurchase.PurchasedOn;
            part.LastSupplierId = newest.PartPurchase.SupplierId;
        }

        await db.SaveChangesAsync(ct);
        return parts.Count;
    }
}
