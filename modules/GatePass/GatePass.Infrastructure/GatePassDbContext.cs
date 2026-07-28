using ErpPlatform.Shared.Persistence;
using GatePass.Domain;
using Microsoft.EntityFrameworkCore;

namespace GatePass.Infrastructure;

/// <summary>The Gate Pass &amp; Demo Goods database (<c>erp_gatepass</c>).</summary>
public class GatePassDbContext(DbContextOptions<GatePassDbContext> options, ICurrentUserService currentUser)
    : ModuleDbContext(options, currentUser)
{
    public DbSet<GatePassRecord> GatePasses => Set<GatePassRecord>();
    public DbSet<GatePassItem> GatePassItems => Set<GatePassItem>();
    public DbSet<DemoIssuance> DemoIssuances => Set<DemoIssuance>();
    public DbSet<DemoIssuanceItem> DemoIssuanceItems => Set<DemoIssuanceItem>();
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

        b.Entity<GatePassRecord>(e =>
        {
            e.HasIndex(x => x.PassNumber).IsUnique();
            e.HasIndex(x => new { x.Direction, x.Status });
            e.HasIndex(x => x.IssuedAtUtc);
            e.HasIndex(x => x.ExpectedReturnOn);
            e.Property(x => x.PassNumber).HasMaxLength(32);
            e.Property(x => x.PersonName).HasMaxLength(200);
            e.Property(x => x.PersonPhone).HasMaxLength(40);
            e.Property(x => x.PersonCnic).HasMaxLength(50);
            e.Property(x => x.CompanyName).HasMaxLength(200);
            e.Property(x => x.VehicleNumber).HasMaxLength(50);
            e.Property(x => x.Department).HasMaxLength(150);
            e.Property(x => x.Purpose).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.ReferenceType).HasMaxLength(50);
            e.Property(x => x.ReferenceNumber).HasMaxLength(64);
            e.Property(x => x.AuthorizedById).HasMaxLength(450);
            e.Property(x => x.AuthorizedByName).HasMaxLength(200);
            e.Property(x => x.CompletedByName).HasMaxLength(200);
            e.Property(x => x.CancellationReason).HasMaxLength(500);
            e.Property(x => x.ReturnReceivedByName).HasMaxLength(200);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<GatePassItem>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(400);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.Property(x => x.Unit).HasMaxLength(32);
            e.Property(x => x.Remarks).HasMaxLength(400);
            e.Property(x => x.Quantity).HasPrecision(12, 2);
            e.HasOne(x => x.GatePassRecord).WithMany(x => x.Items)
                .HasForeignKey(x => x.GatePassRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.GatePassRecord.IsDeleted);
        });

        b.Entity<DemoIssuance>(e =>
        {
            e.HasIndex(x => x.IssuanceNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ExpectedReturnOn);
            e.Property(x => x.IssuanceNumber).HasMaxLength(32);
            e.Property(x => x.CustomerName).HasMaxLength(200);
            e.Property(x => x.CustomerPhone).HasMaxLength(40);
            e.Property(x => x.CustomerReference).HasMaxLength(64);
            e.Property(x => x.Department).HasMaxLength(150);
            e.Property(x => x.ReferenceLetter).HasMaxLength(200);
            e.Property(x => x.IssuedById).HasMaxLength(450);
            e.Property(x => x.IssuedByName).HasMaxLength(200);
            e.Property(x => x.ReceivedByName).HasMaxLength(200);
            e.Property(x => x.ReturnCondition).HasMaxLength(400);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<DemoIssuanceItem>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(400);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.Property(x => x.Accessories).HasMaxLength(400);
            e.Property(x => x.Remarks).HasMaxLength(400);
            e.Property(x => x.Quantity).HasPrecision(12, 2);
            e.HasOne(x => x.DemoIssuance).WithMany(x => x.Items)
                .HasForeignKey(x => x.DemoIssuanceId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.DemoIssuance.IsDeleted);
        });
    }
}
