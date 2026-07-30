using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

public interface IProductService
{
    Task<List<Product>> ListAsync(string? search = null, CancellationToken ct = default);
    Task<Product?> GetAsync(int id, CancellationToken ct = default);
    Task<Product> CreateAsync(Product product, CancellationToken ct = default);
    Task<Product> UpdateAsync(Product product, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a product and every model and accessory under it. Refused while
    /// any of them still holds stock — a product that vanishes with quantity on it
    /// would leave the ledger describing stock that no longer has an owner.
    /// </summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task<ProductModel?> GetModelAsync(int id, CancellationToken ct = default);
    Task<ProductModel> AddModelAsync(int productId, ProductModel model, CancellationToken ct = default);
    Task<ProductModel> UpdateModelAsync(ProductModel model, CancellationToken ct = default);
    /// <summary>Soft-deletes a model and its accessories. Refused while stock remains.</summary>
    Task DeleteModelAsync(int id, CancellationToken ct = default);

    Task<Accessory?> GetAccessoryAsync(int id, CancellationToken ct = default);
    Task<Accessory> AddAccessoryAsync(int productModelId, Accessory accessory, CancellationToken ct = default);
    Task<Accessory> UpdateAccessoryAsync(Accessory accessory, CancellationToken ct = default);
    /// <summary>Soft-deletes an accessory. Refused while stock remains.</summary>
    Task DeleteAccessoryAsync(int id, CancellationToken ct = default);
}

public class ProductService(InventoryDbContext db) : IProductService
{
    public async Task<List<Product>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        var q = db.Products.Include(p => p.Models).ThenInclude(m => m.Accessories)
            .AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(p => p.Name.Contains(s)
                          || (p.Category != null && p.Category.Contains(s))
                          || p.Models.Any(m => m.Name.Contains(s) || (m.Sku != null && m.Sku.Contains(s))));
        }

        return await q.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public Task<Product?> GetAsync(int id, CancellationToken ct = default) =>
        db.Products.Include(p => p.Models).ThenInclude(m => m.Accessories)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Product> CreateAsync(Product product, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("Product name is required.");

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return product;
    }

    public async Task<Product> UpdateAsync(Product product, CancellationToken ct = default)
    {
        var existing = await db.Products.FirstOrDefaultAsync(p => p.Id == product.Id, ct)
            ?? throw new InvalidOperationException("Product not found.");

        existing.Name = product.Name;
        existing.Category = product.Category;
        existing.SkuPrefix = product.SkuPrefix;
        existing.Description = product.Description;
        existing.IsActive = product.IsActive;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var product = await db.Products
            .Include(p => p.Models).ThenInclude(m => m.Accessories)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return;

        var held = product.Models.Sum(m => m.CurrentQuantity)
                   + product.Models.Sum(m => m.Accessories.Sum(a => a.CurrentQuantity));
        if (held != 0)
            throw new InvalidOperationException(
                $"{product.Name} still holds {held} in stock. Move it out before deleting.");

        // Soft delete (ModuleDbContext turns Remove into a flag update), so the
        // stock ledger keeps referring to rows that still exist.
        foreach (var model in product.Models)
        {
            db.Accessories.RemoveRange(model.Accessories);
            db.ProductModels.Remove(model);
        }
        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
    }

    public Task<ProductModel?> GetModelAsync(int id, CancellationToken ct = default) =>
        db.ProductModels.Include(m => m.Accessories).FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task DeleteModelAsync(int id, CancellationToken ct = default)
    {
        var model = await db.ProductModels.Include(m => m.Accessories)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null) return;

        var held = model.CurrentQuantity + model.Accessories.Sum(a => a.CurrentQuantity);
        if (held != 0)
            throw new InvalidOperationException(
                $"{model.Name} still holds {held} in stock. Move it out before deleting.");

        db.Accessories.RemoveRange(model.Accessories);
        db.ProductModels.Remove(model);
        await db.SaveChangesAsync(ct);
    }

    public Task<Accessory?> GetAccessoryAsync(int id, CancellationToken ct = default) =>
        db.Accessories.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task DeleteAccessoryAsync(int id, CancellationToken ct = default)
    {
        var accessory = await db.Accessories.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (accessory is null) return;

        if (accessory.CurrentQuantity != 0)
            throw new InvalidOperationException(
                $"{accessory.Name} still holds {accessory.CurrentQuantity} in stock. " +
                "Move it out before deleting.");

        db.Accessories.Remove(accessory);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProductModel> AddModelAsync(int productId, ProductModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            throw new InvalidOperationException("Model name is required.");

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw new InvalidOperationException("Product not found.");

        model.ProductId = product.Id;
        model.CurrentQuantity = 0;
        db.ProductModels.Add(model);
        await db.SaveChangesAsync(ct);
        return model;
    }

    public async Task<ProductModel> UpdateModelAsync(ProductModel model, CancellationToken ct = default)
    {
        var existing = await db.ProductModels.FirstOrDefaultAsync(m => m.Id == model.Id, ct)
            ?? throw new InvalidOperationException("Model not found.");

        existing.Name = model.Name;
        existing.ModelNumber = model.ModelNumber;
        existing.Sku = model.Sku;
        existing.Unit = model.Unit;
        existing.ReorderThreshold = model.ReorderThreshold;
        existing.IsActive = model.IsActive;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<Accessory> AddAccessoryAsync(int productModelId, Accessory accessory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessory.Name))
            throw new InvalidOperationException("Accessory name is required.");

        var model = await db.ProductModels.FirstOrDefaultAsync(m => m.Id == productModelId, ct)
            ?? throw new InvalidOperationException("Model not found.");

        accessory.ProductModelId = model.Id;
        accessory.CurrentQuantity = 0;
        db.Accessories.Add(accessory);
        await db.SaveChangesAsync(ct);
        return accessory;
    }

    public async Task<Accessory> UpdateAccessoryAsync(Accessory accessory, CancellationToken ct = default)
    {
        var existing = await db.Accessories.FirstOrDefaultAsync(a => a.Id == accessory.Id, ct)
            ?? throw new InvalidOperationException("Accessory not found.");

        existing.Name = accessory.Name;
        existing.Sku = accessory.Sku;
        existing.Unit = accessory.Unit;
        existing.ReorderThreshold = accessory.ReorderThreshold;
        existing.IsActive = accessory.IsActive;

        await db.SaveChangesAsync(ct);
        return existing;
    }
}
