using ErpPlatform.Shared.Persistence;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

/// <summary>The Inventory database (<c>erp_inventory</c>).</summary>
public class InventoryDbContext(DbContextOptions<InventoryDbContext> options, ICurrentUserService currentUser)
    : ModuleDbContext(options, currentUser)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductModel> ProductModels => Set<ProductModel>();
    public DbSet<Accessory> Accessories => Set<Accessory>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Product>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.SkuPrefix).HasMaxLength(32);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<ProductModel>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ModelNumber).HasMaxLength(100);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Unit).HasMaxLength(32);
            e.Property(x => x.ReorderThreshold).HasPrecision(14, 2);
            e.Property(x => x.CurrentQuantity).HasPrecision(14, 2);
            e.HasIndex(x => x.Sku);
            e.HasOne(x => x.Product).WithMany(x => x.Models)
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Product.IsDeleted);
        });

        b.Entity<Accessory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Unit).HasMaxLength(32);
            e.Property(x => x.ReorderThreshold).HasPrecision(14, 2);
            e.Property(x => x.CurrentQuantity).HasPrecision(14, 2);
            e.HasIndex(x => x.Sku);
            e.HasOne(x => x.ProductModel).WithMany(x => x.Accessories)
                .HasForeignKey(x => x.ProductModelId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.ProductModel.IsDeleted);
        });

        b.Entity<StockTransaction>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.BalanceAfter).HasPrecision(14, 2);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.PerformedById).HasMaxLength(450);
            e.Property(x => x.PerformedByName).HasMaxLength(200);
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.HasIndex(x => x.CreatedAtUtc);
        });
    }
}
