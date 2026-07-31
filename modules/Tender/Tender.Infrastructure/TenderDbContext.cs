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

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}
