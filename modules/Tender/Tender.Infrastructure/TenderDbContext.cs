using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Tender.Domain;

namespace Tender.Infrastructure;

/// <summary>The Tender & Project Records database (<c>erp_tender</c>).</summary>
public class TenderDbContext(DbContextOptions<TenderDbContext> options, ICurrentUserService currentUser)
    : ModuleDbContext(options, currentUser)
{
    public DbSet<TenderRecord> Tenders => Set<TenderRecord>();
    public DbSet<TenderGuarantee> Guarantees => Set<TenderGuarantee>();
    public DbSet<TenderDocument> Documents => Set<TenderDocument>();
    public DbSet<TenderCompetitor> Competitors => Set<TenderCompetitor>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<WorkTask> WorkTasks => Set<WorkTask>();
    public DbSet<TenderItem> TenderItems => Set<TenderItem>();
    public DbSet<ProjectMilestone> ProjectMilestones => Set<ProjectMilestone>();
    public DbSet<PhysicalFile> Files => Set<PhysicalFile>();
    public DbSet<FileMovement> FileMovements => Set<FileMovement>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // See InventoryDbContext for why the token is a re-stamped GUID rather than a
        // native rowversion.
        foreach (var type in b.Model.GetEntityTypes()
                     .Where(t => typeof(IConcurrencyChecked).IsAssignableFrom(t.ClrType)))
        {
            b.Entity(type.ClrType).Property(nameof(IConcurrencyChecked.ConcurrencyStamp))
                .IsConcurrencyToken();
        }

        b.Entity<TenderRecord>(e =>
        {
            e.HasIndex(x => x.TenderNumber).IsUnique();
            e.Property(x => x.TenderNumber).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.IssuingAuthority).HasMaxLength(300);
            e.Property(x => x.Department).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.EstimatedValue).HasPrecision(16, 2);
            e.Property(x => x.TenderFee).HasPrecision(16, 2);
            e.Property(x => x.EmdAmount).HasPrecision(16, 2);
            e.Property(x => x.EmdExemptionReason).HasMaxLength(500);
            e.Property(x => x.PerformanceGuaranteePercentage).HasPrecision(5, 2);
            e.Property(x => x.RetentionMoneyPercentage).HasPrecision(5, 2);
            e.Property(x => x.PortalReference).HasMaxLength(200);
            e.Property(x => x.L1Amount).HasPrecision(16, 2);
            e.Property(x => x.AwardedValue).HasPrecision(16, 2);
            e.Property(x => x.WorkOrderNumber).HasMaxLength(100);
            e.Property(x => x.PaymentTerms).HasMaxLength(500);
            e.Property(x => x.ContactPerson).HasMaxLength(200);
            e.Property(x => x.ContactPhone).HasMaxLength(50);
            e.Property(x => x.ContactEmail).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            // Read off the schedule lines, never stored beside them.
            e.Ignore(x => x.ItemsTotal);
            e.Ignore(x => x.HasSchedule);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<TenderGuarantee>(e =>
        {
            e.HasIndex(x => x.TenderRecordId);
            e.HasIndex(x => x.ExpiryDate);
            e.Property(x => x.BankName).HasMaxLength(200);
            e.Property(x => x.BranchName).HasMaxLength(200);
            e.Property(x => x.BankContactPerson).HasMaxLength(200);
            e.Property(x => x.BankContactPhone).HasMaxLength(50);
            e.Property(x => x.GuaranteeNumber).HasMaxLength(100);
            e.Property(x => x.Amount).HasPrecision(16, 2);
            e.Property(x => x.Charges).HasPrecision(16, 2);
            e.Property(x => x.ReleaseReference).HasMaxLength(200);
            e.Property(x => x.Remarks).HasMaxLength(1000);
            e.HasOne(x => x.TenderRecord).WithMany(x => x.Guarantees)
                .HasForeignKey(x => x.TenderRecordId).OnDelete(DeleteBehavior.Cascade);
            // Self-reference for the renewal chain; restrict so deleting a guarantee
            // that something else renewed doesn't cascade into a second delete.
            e.HasOne(x => x.RenewalOfGuarantee).WithMany()
                .HasForeignKey(x => x.RenewalOfGuaranteeId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted && !x.TenderRecord.IsDeleted);
        });

        b.Entity<TenderDocument>(e =>
        {
            e.HasIndex(x => x.TenderRecordId);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.ReferenceNumber).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.TenderRecord).WithMany(x => x.Documents)
                .HasForeignKey(x => x.TenderRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.TenderRecord.IsDeleted);
        });

        b.Entity<TenderCompetitor>(e =>
        {
            e.HasIndex(x => x.TenderRecordId);
            e.Property(x => x.BidderName).HasMaxLength(300);
            e.Property(x => x.QuotedAmount).HasPrecision(16, 2);
            e.Property(x => x.Remarks).HasMaxLength(500);
            e.HasOne(x => x.TenderRecord).WithMany(x => x.Competitors)
                .HasForeignKey(x => x.TenderRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.TenderRecord.IsDeleted);
        });

        b.Entity<Project>(e =>
        {
            e.HasIndex(x => x.ProjectCode).IsUnique();
            e.Property(x => x.ProjectCode).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Client).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.ManagerUserId).HasMaxLength(450);
            e.Property(x => x.ManagerName).HasMaxLength(200);
            e.Property(x => x.ContractValue).HasPrecision(16, 2);
            e.Property(x => x.Budget).HasPrecision(16, 2);
            e.Property(x => x.ContactPerson).HasMaxLength(200);
            e.Property(x => x.ContactPhone).HasMaxLength(50);
            e.Property(x => x.ContactEmail).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            // Derived from the task list — never a column, or the two drift apart.
            e.Ignore(x => x.ProgressPercent);
            e.Ignore(x => x.IsOpen);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<TenderItem>(e =>
        {
            e.HasIndex(x => x.TenderRecordId);
            e.Property(x => x.ItemCode).HasMaxLength(32);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Specification).HasMaxLength(2000);
            e.Property(x => x.Unit).HasMaxLength(32);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.CountryOfOrigin).HasMaxLength(100);
            e.Property(x => x.Remarks).HasMaxLength(1000);
            e.Property(x => x.Quantity).HasPrecision(16, 3);
            e.Property(x => x.UnitRate).HasPrecision(18, 4);
            e.Property(x => x.EstimatedRate).HasPrecision(18, 4);
            e.Property(x => x.CostRate).HasPrecision(18, 4);
            // Derived from quantity x rate — never stored, or the line and its total drift.
            e.Ignore(x => x.Amount);
            e.Ignore(x => x.CostAmount);
            e.Ignore(x => x.Margin);
            e.Ignore(x => x.MarginPercent);
            e.HasOne(x => x.TenderRecord).WithMany(x => x.Items)
                .HasForeignKey(x => x.TenderRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.TenderRecord.IsDeleted);
        });

        b.Entity<WorkTask>(e =>
        {
            e.HasIndex(x => x.TenderRecordId);
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.DueDate);
            e.HasIndex(x => x.AssignedToUserId);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.AssignedToUserId).HasMaxLength(450);
            e.Property(x => x.AssignedToName).HasMaxLength(200);
            e.Property(x => x.EstimatedHours).HasPrecision(10, 2);
            e.Property(x => x.ActualHours).HasPrecision(10, 2);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Ignore(x => x.IsOpen);
            // Two real FKs rather than a type/id pair, so cascade delete still works.
            // "Exactly one is set" is a service rule; the database can't express it.
            e.HasOne(x => x.TenderRecord).WithMany(x => x.Tasks)
                .HasForeignKey(x => x.TenderRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Project).WithMany(x => x.Tasks)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted
                                  && (x.TenderRecord == null || !x.TenderRecord.IsDeleted)
                                  && (x.Project == null || !x.Project.IsDeleted));
        });

        b.Entity<ProjectMilestone>(e =>
        {
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.DueDate);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.PaymentAmount).HasPrecision(16, 2);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.Project).WithMany(x => x.Milestones)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Project.IsDeleted);
        });

        b.Entity<DocumentSequence>(e =>
        {
            e.HasIndex(x => new { x.Type, x.Year }).IsUnique();
            e.Property(x => x.Type).HasMaxLength(50);
            e.Property(x => x.Prefix).HasMaxLength(16);
        });

        b.Entity<PhysicalFile>(e =>
        {
            e.HasIndex(x => x.FileNumber).IsUnique();
            // One file per owner record — the registry falls apart if a tender can
            // sprout a second folder nobody knows about.
            e.HasIndex(x => new { x.OwnerType, x.OwnerId }).IsUnique();
            e.Property(x => x.FileNumber).HasMaxLength(32);
            e.Property(x => x.OwnerReference).HasMaxLength(64);
            e.Property(x => x.OwnerTitle).HasMaxLength(300);
            e.Property(x => x.HolderUserId).HasMaxLength(450);
            e.Property(x => x.HolderName).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.VolumeNumber).HasMaxLength(50);
            e.Property(x => x.Remarks).HasMaxLength(1000);
            e.Ignore(x => x.IsOut);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<FileMovement>(e =>
        {
            e.HasIndex(x => x.PhysicalFileId);
            e.HasIndex(x => x.MovedOn);
            e.Property(x => x.FromHolderName).HasMaxLength(200);
            e.Property(x => x.FromLocation).HasMaxLength(200);
            e.Property(x => x.ToHolderUserId).HasMaxLength(450);
            e.Property(x => x.ToHolderName).HasMaxLength(200);
            e.Property(x => x.ToLocation).HasMaxLength(200);
            e.Property(x => x.Purpose).HasMaxLength(300);
            e.Property(x => x.Remarks).HasMaxLength(1000);
            e.Property(x => x.RecordedById).HasMaxLength(450);
            e.Property(x => x.RecordedByName).HasMaxLength(200);
            e.HasOne(x => x.PhysicalFile).WithMany(x => x.Movements)
                .HasForeignKey(x => x.PhysicalFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.PhysicalFile.IsDeleted);
        });
    }
}
