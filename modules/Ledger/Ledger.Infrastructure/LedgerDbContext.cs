using ErpPlatform.Shared.Persistence;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure;

/// <summary>The plain-ledger database (<c>erp_ledger</c>).</summary>
public class LedgerDbContext(DbContextOptions<LedgerDbContext> options, ICurrentUserService currentUser)
    : ModuleDbContext(options, currentUser)
{
    public DbSet<PlainLedger> Ledgers => Set<PlainLedger>();
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();
    public DbSet<LedgerHead> Heads => Set<LedgerHead>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<PlainLedger>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.ParentLedgerId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.CounterpartyName).HasMaxLength(200);
            e.Property(x => x.CounterpartyPhone).HasMaxLength(40);
            e.Property(x => x.CounterpartyAddress).HasMaxLength(400);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.OpeningBalance).HasPrecision(18, 2);

            // Restrict, not Cascade: a parent with sub-ledgers under it must be
            // emptied deliberately rather than silently taking its children — the
            // service refuses the delete and says which children are in the way.
            e.HasOne(x => x.ParentLedger).WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentLedgerId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Head).WithMany()
                .HasForeignKey(x => x.HeadId).OnDelete(DeleteBehavior.SetNull);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.HasIndex(x => x.PlainLedgerId);
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.TransferGroup);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.RecordedById).HasMaxLength(450);
            e.Property(x => x.RecordedByName).HasMaxLength(200);
            e.Ignore(x => x.Signed);

            e.HasOne(x => x.PlainLedger).WithMany(x => x.Entries)
                .HasForeignKey(x => x.PlainLedgerId).OnDelete(DeleteBehavior.Cascade);

            // No cascade from the counter side, or deleting one ledger would take
            // the other half of a transfer with it and leave the pair lopsided.
            e.HasOne(x => x.CounterLedger).WithMany()
                .HasForeignKey(x => x.CounterLedgerId).OnDelete(DeleteBehavior.Restrict);

            // SetNull, so retiring a head never takes the money with it: the entry
            // stays and simply reads as unclassified.
            e.HasOne(x => x.Head).WithMany()
                .HasForeignKey(x => x.HeadId).OnDelete(DeleteBehavior.SetNull);

            e.HasQueryFilter(x => !x.IsDeleted && !x.PlainLedger.IsDeleted);
        });

        b.Entity<LedgerHead>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.ParentHeadId);
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Notes).HasMaxLength(1000);

            // Restrict: a head with sub-heads must be emptied deliberately.
            e.HasOne(x => x.ParentHead).WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentHeadId).OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}
