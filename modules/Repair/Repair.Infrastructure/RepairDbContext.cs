using ErpPlatform.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

/// <summary>The Repair module's database (<c>erp_repair</c>).</summary>
public class RepairDbContext(DbContextOptions<RepairDbContext> options, ICurrentUserService currentUser)
    : ModuleDbContext(options, currentUser)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Intake> Intakes => Set<Intake>();
    public DbSet<RepairJob> RepairJobs => Set<RepairJob>();
    public DbSet<JobStatusHistory> JobStatusHistories => Set<JobStatusHistory>();
    public DbSet<Symptom> Symptoms => Set<Symptom>();
    public DbSet<JobSymptom> JobSymptoms => Set<JobSymptom>();
    public DbSet<Accessory> Accessories => Set<Accessory>();
    public DbSet<JobAccessory> JobAccessories => Set<JobAccessory>();
    public DbSet<Diagnosis> Diagnoses => Set<Diagnosis>();
    public DbSet<JobPhoto> JobPhotos => Set<JobPhoto>();
    public DbSet<JobWorkItem> JobWorkItems => Set<JobWorkItem>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PartPurchase> PartPurchases => Set<PartPurchase>();
    public DbSet<PartPurchaseItem> PartPurchaseItems => Set<PartPurchaseItem>();
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<QuotationItem> QuotationItems => Set<QuotationItem>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<DeviceType> DeviceTypes => Set<DeviceType>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<DocumentSequence>(e =>
        {
            e.HasIndex(x => new { x.Type, x.Year }).IsUnique();
            e.Property(x => x.Type).HasMaxLength(50);
            e.Property(x => x.Prefix).HasMaxLength(16);
        });

        b.Entity<Customer>(e =>
        {
            e.HasIndex(x => x.Phone);
            e.HasIndex(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Organization).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Intake>(e =>
        {
            e.HasIndex(x => x.IntakeNumber).IsUnique();
            e.Property(x => x.IntakeNumber).HasMaxLength(32);
            e.Property(x => x.ReceivedById).HasMaxLength(450);
            e.Property(x => x.ReceivedByName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasOne(x => x.Customer).WithMany(x => x.Intakes)
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Customer.IsDeleted);
        });

        b.Entity<RepairJob>(e =>
        {
            e.HasIndex(x => x.JobNumber).IsUnique();
            e.HasIndex(x => x.SerialNumber);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.AssignedTechnicianId);
            e.Property(x => x.JobNumber).HasMaxLength(32);
            e.Property(x => x.AssignedTechnicianId).HasMaxLength(450);
            e.Property(x => x.AssignedTechnicianName).HasMaxLength(200);
            e.Property(x => x.DeviceName).HasMaxLength(200);
            e.Property(x => x.Brand).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.Property(x => x.IssueDescription).HasMaxLength(2000);
            e.Property(x => x.DeliveredToName).HasMaxLength(200);
            e.Property(x => x.DeliveredToPhone).HasMaxLength(40);
            e.Property(x => x.DeliveredToCnic).HasMaxLength(50);
            e.Property(x => x.DeliveredByName).HasMaxLength(200);
            e.Property(x => x.DeliveryNote).HasMaxLength(1000);
            e.HasOne(x => x.Intake).WithMany(x => x.Jobs)
                .HasForeignKey(x => x.IntakeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<JobStatusHistory>(e =>
        {
            e.Property(x => x.ChangedById).HasMaxLength(450);
            e.Property(x => x.ChangedByName).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasOne(x => x.RepairJob).WithMany(x => x.StatusHistory)
                .HasForeignKey(x => x.RepairJobId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.RepairJob.IsDeleted);
        });

        b.Entity<Symptom>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(100);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<JobSymptom>(e =>
        {
            e.HasIndex(x => new { x.RepairJobId, x.SymptomId }).IsUnique();
            e.HasOne(x => x.RepairJob).WithMany(x => x.Symptoms)
                .HasForeignKey(x => x.RepairJobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Symptom).WithMany()
                .HasForeignKey(x => x.SymptomId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.RepairJob.IsDeleted && !x.Symptom.IsDeleted);
        });

        b.Entity<Accessory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<JobAccessory>(e =>
        {
            e.Property(x => x.Note).HasMaxLength(400);
            e.HasOne(x => x.RepairJob).WithMany(x => x.Accessories)
                .HasForeignKey(x => x.RepairJobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Accessory).WithMany()
                .HasForeignKey(x => x.AccessoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.RepairJob.IsDeleted && !x.Accessory.IsDeleted);
        });

        b.Entity<Diagnosis>(e =>
        {
            e.Property(x => x.TechnicianId).HasMaxLength(450);
            e.Property(x => x.TechnicianName).HasMaxLength(200);
            e.Property(x => x.Findings).HasMaxLength(4000);
            e.Property(x => x.RequiredParts).HasMaxLength(2000);
            e.Property(x => x.RequiredLabor).HasMaxLength(2000);
            e.Property(x => x.WorkPerformed).HasMaxLength(4000);
            e.Property(x => x.InternalNotes).HasMaxLength(2000);
            e.Property(x => x.EstimatedHours).HasPrecision(8, 2);
            e.HasOne(x => x.RepairJob).WithMany(x => x.Diagnoses)
                .HasForeignKey(x => x.RepairJobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Part).WithMany()
                .HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted && !x.RepairJob.IsDeleted);
        });

        b.Entity<JobWorkItem>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(400);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.Quantity).HasPrecision(12, 2);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.HasOne(x => x.RepairJob).WithMany(x => x.WorkItems)
                .HasForeignKey(x => x.RepairJobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Part).WithMany()
                .HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted && !x.RepairJob.IsDeleted);
        });

        b.Entity<JobPhoto>(e =>
        {
            e.Property(x => x.UploadedById).HasMaxLength(450);
            e.Property(x => x.Path).HasMaxLength(400);
            e.Property(x => x.Caption).HasMaxLength(400);
            e.HasOne(x => x.RepairJob).WithMany(x => x.Photos)
                .HasForeignKey(x => x.RepairJobId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.RepairJob.IsDeleted);
        });

        b.Entity<Part>(e =>
        {
            e.HasIndex(x => x.Sku).IsUnique();
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Brand).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.LastPurchaseCost).HasPrecision(18, 4);
            e.Property(x => x.AverageCost).HasPrecision(18, 4);
            e.Property(x => x.PurchasedQuantity).HasPrecision(14, 2);
            e.Ignore(x => x.MarginPercent);
            e.HasOne(x => x.LastSupplier).WithMany()
                .HasForeignKey(x => x.LastSupplierId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Supplier>(e =>
        {
            e.HasIndex(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.TaxNumber).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<PartPurchase>(e =>
        {
            e.HasIndex(x => x.PurchaseNumber).IsUnique();
            e.HasIndex(x => x.PurchasedOn);
            e.Property(x => x.PurchaseNumber).HasMaxLength(32);
            e.Property(x => x.SupplierInvoiceNumber).HasMaxLength(100);
            e.Property(x => x.ReceivedById).HasMaxLength(450);
            e.Property(x => x.ReceivedByName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            foreach (var money in new[] { "Subtotal", "TaxAmount", "DiscountAmount",
                                          "OtherCharges", "TotalAmount" })
                e.Property(money).HasPrecision(18, 2);
            e.HasOne(x => x.Supplier).WithMany()
                .HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<PartPurchaseItem>(e =>
        {
            e.HasIndex(x => x.PartId);
            e.Property(x => x.Quantity).HasPrecision(14, 2);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.Property(x => x.NewSellingPrice).HasPrecision(18, 2);
            e.Property(x => x.Remarks).HasMaxLength(400);
            e.HasOne(x => x.PartPurchase).WithMany(x => x.Items)
                .HasForeignKey(x => x.PartPurchaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Part).WithMany()
                .HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.PartPurchase.IsDeleted);
        });

        b.Entity<Quotation>(e =>
        {
            e.HasIndex(x => x.QuotationNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.QuotationNumber).HasMaxLength(32);
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(10);
            e.Property(x => x.Project).HasMaxLength(150);
            e.Property(x => x.PreparedById).HasMaxLength(450);
            e.Property(x => x.PreparedByName).HasMaxLength(200);
            e.Property(x => x.ManagerId).HasMaxLength(450);
            e.Property(x => x.LaborDescription).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(2000);
            foreach (var money in new[] { "LaborAmount", "PartsAmount", "Subtotal", "TaxAmount",
                                          "DiscountAmount", "TotalAmount" })
                e.Property(money).HasPrecision(18, 2);
            e.Property(x => x.TaxPercent).HasPrecision(8, 4);
            e.HasOne(x => x.RepairJob).WithMany()
                .HasForeignKey(x => x.RepairJobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Intake).WithMany()
                .HasForeignKey(x => x.IntakeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<QuotationItem>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(400);
            e.Property(x => x.Quantity).HasPrecision(12, 2);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.Discount).HasPrecision(18, 2);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.HasOne(x => x.Quotation).WithMany(x => x.Items)
                .HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Part).WithMany()
                .HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.Quotation.IsDeleted);
        });

        b.Entity<SalesOrder>(e =>
        {
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => x.PaymentStatus);
            e.Property(x => x.OrderNumber).HasMaxLength(32);
            e.Property(x => x.FinalizedById).HasMaxLength(450);
            e.Property(x => x.FinalizedByName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            foreach (var money in new[] { "LaborAmount", "PartsAmount", "TaxAmount",
                                          "DiscountAmount", "TotalAmount", "AmountPaid" })
                e.Property(money).HasPrecision(18, 2);
            e.HasOne(x => x.Quotation).WithMany()
                .HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.RecordedById).HasMaxLength(450);
            e.Property(x => x.RecordedByName).HasMaxLength(200);
            e.Property(x => x.ReferenceNumber).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.SalesOrder).WithMany(x => x.Payments)
                .HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.SalesOrder.IsDeleted);
        });

        b.Entity<Brand>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<DeviceType>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}
