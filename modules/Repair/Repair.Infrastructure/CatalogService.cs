using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

/// <summary>
/// Parts inventory and the dropdown catalogs (symptoms, accessories, brands,
/// device types) that the Laravel app kept behind its admin settings screen.
/// </summary>
public interface ICatalogService
{
    Task<List<Part>> ListPartsAsync(string? search = null, bool lowStockOnly = false,
        CancellationToken ct = default);
    Task<Part?> GetPartAsync(int id, CancellationToken ct = default);
    Task<Part> SavePartAsync(Part part, CancellationToken ct = default);
    Task DeletePartAsync(int id, CancellationToken ct = default);
    /// <summary>Adjusts stock by a signed delta; refuses to go negative.</summary>
    Task AdjustStockAsync(int partId, int delta, CancellationToken ct = default);

    Task<List<Symptom>> ListSymptomsAsync(CancellationToken ct = default);
    Task<List<Accessory>> ListAccessoriesAsync(CancellationToken ct = default);
    Task<List<Brand>> ListBrandsAsync(CancellationToken ct = default);
    Task<List<DeviceType>> ListDeviceTypesAsync(CancellationToken ct = default);

    Task AddSymptomAsync(string name, string? category, CancellationToken ct = default);
    Task AddAccessoryAsync(string name, CancellationToken ct = default);
    Task AddBrandAsync(string name, CancellationToken ct = default);
    Task AddDeviceTypeAsync(string name, CancellationToken ct = default);
    Task RemoveCatalogEntryAsync(string kind, int id, CancellationToken ct = default);
}

public class CatalogService(RepairDbContext db) : ICatalogService
{
    /// <summary>Parts at or below this level show up on the low-stock filter.</summary>
    public const int LowStockThreshold = 3;

    public async Task<List<Part>> ListPartsAsync(
        string? search = null, bool lowStockOnly = false, CancellationToken ct = default)
    {
        var q = db.Parts.AsNoTracking().AsQueryable();

        if (lowStockOnly) q = q.Where(p => p.StockQuantity <= LowStockThreshold);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(p => p.Name.Contains(s)
                          || (p.Sku != null && p.Sku.Contains(s))
                          || (p.Brand != null && p.Brand.Contains(s))
                          || (p.Model != null && p.Model.Contains(s)));
        }

        return await q.OrderBy(p => p.Name).Take(500).ToListAsync(ct);
    }

    public Task<Part?> GetPartAsync(int id, CancellationToken ct = default) =>
        db.Parts.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Part> SavePartAsync(Part part, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(part.Name))
            throw new InvalidOperationException("Part name is required.");
        if (part.Price < 0)
            throw new InvalidOperationException("Price can't be negative.");
        if (part.StockQuantity < 0)
            throw new InvalidOperationException("Stock can't be negative.");

        part.Sku = string.IsNullOrWhiteSpace(part.Sku) ? null : part.Sku.Trim();
        if (part.Sku is not null &&
            await db.Parts.AnyAsync(p => p.Sku == part.Sku && p.Id != part.Id, ct))
            throw new InvalidOperationException($"SKU {part.Sku} is already in use.");

        if (part.Id == 0) db.Parts.Add(part);
        await db.SaveChangesAsync(ct);
        return part;
    }

    public async Task DeletePartAsync(int id, CancellationToken ct = default)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return;
        db.Parts.Remove(part);
        await db.SaveChangesAsync(ct);
    }

    public async Task AdjustStockAsync(int partId, int delta, CancellationToken ct = default)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId, ct)
                   ?? throw new InvalidOperationException("Part not found.");

        if (part.StockQuantity + delta < 0)
            throw new InvalidOperationException(
                $"Only {part.StockQuantity} of {part.Name} in stock.");

        part.StockQuantity += delta;
        await db.SaveChangesAsync(ct);
    }

    public Task<List<Symptom>> ListSymptomsAsync(CancellationToken ct = default) =>
        db.Symptoms.AsNoTracking().OrderBy(s => s.Category).ThenBy(s => s.Name).ToListAsync(ct);

    public Task<List<Accessory>> ListAccessoriesAsync(CancellationToken ct = default) =>
        db.Accessories.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);

    public Task<List<Brand>> ListBrandsAsync(CancellationToken ct = default) =>
        db.Brands.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);

    public Task<List<DeviceType>> ListDeviceTypesAsync(CancellationToken ct = default) =>
        db.DeviceTypes.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);

    public async Task AddSymptomAsync(string name, string? category, CancellationToken ct = default)
    {
        Require(name);
        db.Symptoms.Add(new Symptom { Name = name.Trim(), Category = category });
        await db.SaveChangesAsync(ct);
    }

    public async Task AddAccessoryAsync(string name, CancellationToken ct = default)
    {
        Require(name);
        db.Accessories.Add(new Accessory { Name = name.Trim() });
        await db.SaveChangesAsync(ct);
    }

    public async Task AddBrandAsync(string name, CancellationToken ct = default)
    {
        Require(name);
        db.Brands.Add(new Brand { Name = name.Trim() });
        await db.SaveChangesAsync(ct);
    }

    public async Task AddDeviceTypeAsync(string name, CancellationToken ct = default)
    {
        Require(name);
        db.DeviceTypes.Add(new DeviceType { Name = name.Trim() });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveCatalogEntryAsync(string kind, int id, CancellationToken ct = default)
    {
        switch (kind)
        {
            case "symptom":
                if (await db.Symptoms.FirstOrDefaultAsync(x => x.Id == id, ct) is { } s)
                    db.Symptoms.Remove(s);
                break;
            case "accessory":
                if (await db.Accessories.FirstOrDefaultAsync(x => x.Id == id, ct) is { } a)
                    db.Accessories.Remove(a);
                break;
            case "brand":
                if (await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct) is { } b)
                    db.Brands.Remove(b);
                break;
            case "device":
                if (await db.DeviceTypes.FirstOrDefaultAsync(x => x.Id == id, ct) is { } d)
                    db.DeviceTypes.Remove(d);
                break;
            default:
                throw new ArgumentException($"Unknown catalog '{kind}'.", nameof(kind));
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Require(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("A name is required.");
    }
}
