using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ErpPlatform.Shared.Identity;

/// <summary>
/// The single shared identity database (<c>erp_identity</c>). Holds users, roles,
/// permission claims, the module catalog and per-user module access — and nothing
/// else. No business module writes to it.
/// </summary>
public class PlatformIdentityDbContext(DbContextOptions<PlatformIdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, string>(options)
{
    public DbSet<ModuleRecord> Modules => Set<ModuleRecord>();
    public DbSet<UserModuleAccess> UserModuleAccess => Set<UserModuleAccess>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.EmployeeCode).HasMaxLength(50);
            e.Property(x => x.ManagerId).HasMaxLength(450);
            e.HasIndex(x => x.EmployeeCode);
        });

        b.Entity<ApplicationRole>(e =>
        {
            e.Property(x => x.ModuleKey).HasMaxLength(50);
            e.Property(x => x.Description).HasMaxLength(300);
            e.HasIndex(x => x.ModuleKey);
        });

        b.Entity<ModuleRecord>(e =>
        {
            e.ToTable("Modules");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.BasePath).HasMaxLength(100);
            e.Property(x => x.Icon).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(20);
        });

        b.Entity<UserModuleAccess>(e =>
        {
            e.ToTable("UserModuleAccess");
            e.HasIndex(x => new { x.UserId, x.ModuleKey }).IsUnique();
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.ModuleKey).HasMaxLength(50);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
        });
    }
}
