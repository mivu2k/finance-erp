using Auto.Domain;
using ErpPlatform.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auto.Infrastructure;

/// <summary>The Auto (vehicle fleet) database (<c>erp_auto</c>).</summary>
public class AutoDbContext(DbContextOptions<AutoDbContext> options, ICurrentUserService currentUser)
    : ModuleDbContext(options, currentUser)
{
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Vehicle>(e =>
        {
            e.HasIndex(x => x.RegistrationNumber).IsUnique();
            e.Property(x => x.Make).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.RegistrationNumber).HasMaxLength(32);
            e.Property(x => x.Vin).HasMaxLength(64);
            e.Property(x => x.Color).HasMaxLength(50);
            e.Property(x => x.CurrentOdometer).HasPrecision(12, 1);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<MaintenanceRecord>(e =>
        {
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.NextDueDate);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.VendorName).HasMaxLength(200);
            e.Property(x => x.Cost).HasPrecision(14, 2);
            e.Property(x => x.OdometerAtService).HasPrecision(12, 1);
            e.Property(x => x.NextDueOdometer).HasPrecision(12, 1);
            e.Property(x => x.PerformedById).HasMaxLength(450);
            e.Property(x => x.PerformedByName).HasMaxLength(200);
            e.HasOne(x => x.Vehicle).WithMany(x => x.MaintenanceRecords)
                .HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Vehicle.IsDeleted);
        });
    }
}
