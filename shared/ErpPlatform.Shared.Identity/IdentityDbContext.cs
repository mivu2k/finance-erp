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
    /// <summary>Platform-wide letterhead — one row, read by every module's print stack.</summary>
    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();
    /// <summary>Sticker layouts — shared, because label stock belongs to the printer.</summary>
    public DbSet<LabelTemplate> LabelTemplates => Set<LabelTemplate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<LabelTemplate>(e =>
        {
            e.HasIndex(x => new { x.DocumentType, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.DocumentType).HasMaxLength(64);
            e.Property(x => x.FieldKeys).HasMaxLength(1000);
            e.Property(x => x.ModifiedBy).HasMaxLength(256);
            e.Property(x => x.WidthMm).HasPrecision(8, 2);
            e.Property(x => x.HeightMm).HasPrecision(8, 2);
            e.Property(x => x.MarginMm).HasPrecision(6, 2);
            e.Property(x => x.FontScale).HasPrecision(4, 2);
        });

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

        b.Entity<CompanyProfile>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Tagline).HasMaxLength(300);
            e.Property(x => x.AddressLine1).HasMaxLength(200);
            e.Property(x => x.AddressLine2).HasMaxLength(200);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Phone).HasMaxLength(60);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Website).HasMaxLength(200);
            e.Property(x => x.TaxNumber).HasMaxLength(60);
            e.Property(x => x.FooterNote).HasMaxLength(600);
            e.Property(x => x.LogoContentType).HasMaxLength(100);
            e.Property(x => x.LogoFileName).HasMaxLength(260);
            e.Property(x => x.ModifiedBy).HasMaxLength(200);
            e.Ignore(x => x.HasLogo);
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
