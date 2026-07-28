using ErpPlatform.Shared.Persistence;
using Hr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hr.Infrastructure;

/// <summary>
/// The HR module's database (<c>erp_hr</c>). Inherits the platform audit pipeline
/// and soft-delete behaviour from <see cref="ModuleDbContext"/>.
/// </summary>
public class HrDbContext(DbContextOptions<HrDbContext> options, ICurrentUserService currentUser)
    : ModuleDbContext(options, currentUser)
{
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Designation> Designations => Set<Designation>();
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

        b.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.EmployeeCode).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.EmployeeCode).HasMaxLength(50);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.FatherName).HasMaxLength(200);
            e.Property(x => x.NationalId).HasMaxLength(50);
            e.Property(x => x.BloodGroup).HasMaxLength(8);
            e.Property(x => x.PhotoPath).HasMaxLength(400);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.AlternatePhone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.EmergencyContactName).HasMaxLength(200);
            e.Property(x => x.EmergencyContactPhone).HasMaxLength(40);
            e.Property(x => x.ReportsToEmployeeCode).HasMaxLength(50);
            e.Property(x => x.WorkLocation).HasMaxLength(150);
            e.Property(x => x.BankName).HasMaxLength(150);
            e.Property(x => x.BankAccountNumber).HasMaxLength(64);
            e.Property(x => x.BankAccountTitle).HasMaxLength(200);
            e.Property(x => x.TaxNumber).HasMaxLength(64);
            e.Property(x => x.SocialSecurityNumber).HasMaxLength(64);
            e.Property(x => x.LeavingReason).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasOne(x => x.Department).WithMany()
                .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Designation).WithMany()
                .HasForeignKey(x => x.DesignationId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<EmployeeDocument>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.FilePath).HasMaxLength(400);
            e.Property(x => x.ContentType).HasMaxLength(150);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => x.ExpiresOn);
            e.HasOne(x => x.Employee).WithMany(x => x.Documents)
                .HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Employee.IsDeleted);
        });

        b.Entity<Department>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.HeadEmployeeCode).HasMaxLength(50);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Designation>(e =>
        {
            e.HasIndex(x => x.Title).IsUnique();
            e.Property(x => x.Title).HasMaxLength(150);
            e.Property(x => x.Grade).HasMaxLength(32);
            e.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}
