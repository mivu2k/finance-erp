using ErpPlatform.Shared.Persistence;
using Inventory.Domain;
using ErpPlatform.Shared.Kernel;
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
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferLine> StockTransferLines => Set<StockTransferLine>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryLine> DeliveryLines => Set<DeliveryLine>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Optimistic locking on the records where a lost update costs stock or money.
        // MariaDB has no native rowversion; ModuleDbContext re-stamps the GUID on save
        // and EF compares the original value in the UPDATE's WHERE clause.
        foreach (var type in b.Model.GetEntityTypes()
                     .Where(t => typeof(IConcurrencyChecked).IsAssignableFrom(t.ClrType)))
        {
            b.Entity(type.ClrType).Property(nameof(IConcurrencyChecked.ConcurrencyStamp))
                .IsConcurrencyToken();
        }

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

        b.Entity<Warehouse>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.Code);
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Address).HasMaxLength(400);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<StockBalance>(e =>
        {
            // One row per item per place; the pair is the identity of the balance.
            e.HasIndex(x => new { x.ItemType, x.ItemId, x.WarehouseId }).IsUnique();
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.HasOne(x => x.Warehouse).WithMany()
                .HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockTransfer>(e =>
        {
            e.HasIndex(x => x.TransferNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.TransferNumber).HasMaxLength(32);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.RaisedById).HasMaxLength(450);
            e.Property(x => x.RaisedByName).HasMaxLength(200);
            e.Property(x => x.DispatchedByName).HasMaxLength(200);
            e.Property(x => x.ReceivedByName).HasMaxLength(200);

            // Restrict both ends: a warehouse with transfer history can't be deleted
            // out from under it.
            e.HasOne(x => x.FromWarehouse).WithMany()
                .HasForeignKey(x => x.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToWarehouse).WithMany()
                .HasForeignKey(x => x.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<StockTransferLine>(e =>
        {
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.Property(x => x.ItemName).HasMaxLength(300);
            e.Property(x => x.SerialNumbers).HasMaxLength(2000);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.ReceivedQuantity).HasPrecision(14, 2);
            e.Ignore(x => x.Shortfall);
            e.HasOne(x => x.StockTransfer).WithMany(x => x.Lines)
                .HasForeignKey(x => x.StockTransferId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.StockTransfer.IsDeleted);
        });

        b.Entity<Supplier>(e =>
        {
            e.HasIndex(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.ContactPerson).HasMaxLength(150);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(400);
            e.Property(x => x.TaxNumber).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<PurchaseOrder>(e =>
        {
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.OrderNumber).HasMaxLength(32);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.RaisedById).HasMaxLength(450);
            e.Property(x => x.RaisedByName).HasMaxLength(200);
            foreach (var money in new[] { "Subtotal", "TaxAmount", "OtherCharges", "DiscountAmount", "TotalAmount" })
                e.Property(money).HasPrecision(18, 2);
            e.Ignore(x => x.IsFullyReceived);
            e.HasOne(x => x.Supplier).WithMany()
                .HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<PurchaseOrderLine>(e =>
        {
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.Property(x => x.ItemName).HasMaxLength(300);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.ReceivedQuantity).HasPrecision(14, 2);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.Ignore(x => x.Outstanding);
            e.HasOne(x => x.PurchaseOrder).WithMany(x => x.Lines)
                .HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.PurchaseOrder.IsDeleted);
        });

        b.Entity<GoodsReceipt>(e =>
        {
            e.HasIndex(x => x.ReceiptNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.ReceiptNumber).HasMaxLength(32);
            e.Property(x => x.SupplierDocumentNumber).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.ReceivedById).HasMaxLength(450);
            e.Property(x => x.ReceivedByName).HasMaxLength(200);
            e.Property(x => x.TotalCost).HasPrecision(18, 2);
            e.HasOne(x => x.Supplier).WithMany()
                .HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PurchaseOrder).WithMany()
                .HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<GoodsReceiptLine>(e =>
        {
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.Property(x => x.ItemName).HasMaxLength(300);
            e.Property(x => x.SerialNumbers).HasMaxLength(4000);
            e.Property(x => x.BatchNumber).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.HasOne(x => x.GoodsReceipt).WithMany(x => x.Lines)
                .HasForeignKey(x => x.GoodsReceiptId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.GoodsReceipt.IsDeleted);
        });

        b.Entity<Customer>(e =>
        {
            e.HasIndex(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.ContactPerson).HasMaxLength(150);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(400);
            e.Property(x => x.TaxNumber).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<SalesOrder>(e =>
        {
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.OrderNumber).HasMaxLength(32);
            e.Property(x => x.CustomerReference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.RaisedById).HasMaxLength(450);
            e.Property(x => x.RaisedByName).HasMaxLength(200);
            foreach (var money in new[] { "Subtotal", "TaxAmount", "OtherCharges", "DiscountAmount", "TotalAmount" })
                e.Property(money).HasPrecision(18, 2);
            e.Ignore(x => x.IsFullyDelivered);
            e.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<SalesOrderLine>(e =>
        {
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.Property(x => x.ItemName).HasMaxLength(300);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.DeliveredQuantity).HasPrecision(14, 2);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.Ignore(x => x.Outstanding);
            e.HasOne(x => x.SalesOrder).WithMany(x => x.Lines)
                .HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.SalesOrder.IsDeleted);
        });

        b.Entity<Delivery>(e =>
        {
            e.HasIndex(x => x.DeliveryNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.DeliveryNumber).HasMaxLength(32);
            e.Property(x => x.ReceivedByName).HasMaxLength(200);
            e.Property(x => x.VehicleNumber).HasMaxLength(50);
            e.Property(x => x.DeliveryAddress).HasMaxLength(400);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.DeliveredById).HasMaxLength(450);
            e.Property(x => x.DeliveredByName).HasMaxLength(200);
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.Property(x => x.TotalCost).HasPrecision(18, 2);
            e.Ignore(x => x.GrossProfit);
            e.Ignore(x => x.MarginPercent);
            e.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SalesOrder).WithMany()
                .HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<DeliveryLine>(e =>
        {
            e.HasIndex(x => new { x.ItemType, x.ItemId });
            e.Property(x => x.ItemName).HasMaxLength(300);
            e.Property(x => x.SerialNumbers).HasMaxLength(4000);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.LineCost).HasPrecision(18, 2);
            e.HasOne(x => x.Delivery).WithMany(x => x.Lines)
                .HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.Delivery.IsDeleted);
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
