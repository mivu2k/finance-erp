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
    public DbSet<StockUnit> StockUnits => Set<StockUnit>();
    public DbSet<StockBatch> StockBatches => Set<StockBatch>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

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
            e.Property(x => x.ReorderQuantity).HasPrecision(14, 2);
            e.Property(x => x.CurrentQuantity).HasPrecision(14, 2);
            e.Property(x => x.PurchasedQuantity).HasPrecision(14, 2);
            e.Property(x => x.SalePrice).HasPrecision(18, 2);
            e.Property(x => x.LastPurchaseCost).HasPrecision(18, 4);
            e.Property(x => x.AverageCost).HasPrecision(18, 4);
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Ignore(x => x.StockValue);
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
            e.Property(x => x.ReorderQuantity).HasPrecision(14, 2);
            e.Property(x => x.CurrentQuantity).HasPrecision(14, 2);
            e.Property(x => x.PurchasedQuantity).HasPrecision(14, 2);
            e.Property(x => x.SalePrice).HasPrecision(18, 2);
            e.Property(x => x.LastPurchaseCost).HasPrecision(18, 4);
            e.Property(x => x.AverageCost).HasPrecision(18, 4);
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Ignore(x => x.StockValue);
            e.HasIndex(x => x.Sku);
            e.HasOne(x => x.ProductModel).WithMany(x => x.Accessories)
                .HasForeignKey(x => x.ProductModelId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.ProductModel.IsDeleted);
        });

        b.Entity<StockUnit>(e =>
        {
            // Unique per item, not globally: two different models may legitimately
            // carry the same manufacturer serial.
            e.HasIndex(x => new { x.ItemType, x.ItemId, x.SerialNumber }).IsUnique();
            e.HasIndex(x => x.SerialNumber);
            e.HasIndex(x => x.Status);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.Property(x => x.IssuedTo).HasMaxLength(200);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Ignore(x => x.CountsAsStock);
            e.HasOne(x => x.StockBatch).WithMany()
                .HasForeignKey(x => x.StockBatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<StockBatch>(e =>
        {
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.HasIndex(x => x.ExpiresOn);
            e.Property(x => x.BatchNumber).HasMaxLength(64);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.RemainingQuantity).HasPrecision(14, 2);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<StockCount>(e =>
        {
            e.HasIndex(x => x.CountNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.CountNumber).HasMaxLength(32);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CountedById).HasMaxLength(450);
            e.Property(x => x.CountedByName).HasMaxLength(200);
            e.Ignore(x => x.VarianceCount);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<StockCountLine>(e =>
        {
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.Property(x => x.ItemName).HasMaxLength(300);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.SystemQuantity).HasPrecision(14, 2);
            e.Property(x => x.CountedQuantity).HasPrecision(14, 2);
            e.Ignore(x => x.Variance);
            e.HasOne(x => x.StockCount).WithMany(x => x.Lines)
                .HasForeignKey(x => x.StockCountId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.StockCount.IsDeleted);
        });

        b.Entity<DocumentSequence>(e =>
        {
            e.HasIndex(x => new { x.Type, x.Year }).IsUnique();
            e.Property(x => x.Type).HasMaxLength(50);
            e.Property(x => x.Prefix).HasMaxLength(16);
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
