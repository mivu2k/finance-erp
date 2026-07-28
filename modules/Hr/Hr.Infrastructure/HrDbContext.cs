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

    public DbSet<BiometricDevice> BiometricDevices => Set<BiometricDevice>();
    public DbSet<AttendancePunch> AttendancePunches => Set<AttendancePunch>();
    public DbSet<AttendanceDay> AttendanceDays => Set<AttendanceDay>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();

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
            e.HasOne(x => x.Shift).WithMany()
                .HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DeviceUserId).HasMaxLength(64);
            e.HasIndex(x => x.DeviceUserId);
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

        b.Entity<BiometricDevice>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.Property(x => x.Location).HasMaxLength(150);
            e.Property(x => x.LastSyncResult).HasMaxLength(500);
            e.HasIndex(x => new { x.Host, x.Port }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<AttendancePunch>(e =>
        {
            // The natural key of a read. Re-pulling the device log is the normal
            // case, so the unique index is what makes sync idempotent.
            e.HasIndex(x => new { x.DeviceUserId, x.PunchedAt, x.BiometricDeviceId })
                .IsUnique()
                .HasDatabaseName("IX_AttendancePunches_Natural");
            e.HasIndex(x => new { x.EmployeeId, x.PunchedAt });
            e.Property(x => x.DeviceUserId).HasMaxLength(64);
            e.HasOne(x => x.BiometricDevice).WithMany()
                .HasForeignKey(x => x.BiometricDeviceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Employee).WithMany()
                .HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AttendanceDay>(e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.Date }).IsUnique();
            e.HasIndex(x => x.Date);
            e.Property(x => x.OverriddenById).HasMaxLength(450);
            e.Property(x => x.OverriddenByName).HasMaxLength(200);
            e.Property(x => x.OverrideReason).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.Employee).WithMany()
                .HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.LeaveRequest).WithMany()
                .HasForeignKey(x => x.LeaveRequestId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Shift>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Holiday>(e =>
        {
            e.HasIndex(x => x.Date).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<LeaveType>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.Colour).HasMaxLength(20);
            e.Property(x => x.AnnualQuota).HasPrecision(6, 2);
            e.Property(x => x.MaxCarryForward).HasPrecision(6, 2);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<LeaveRequest>(e =>
        {
            e.HasIndex(x => x.RequestNumber).IsUnique();
            e.HasIndex(x => new { x.EmployeeId, x.FromDate });
            e.HasIndex(x => x.Status);
            e.Property(x => x.RequestNumber).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.ContactDuringLeave).HasMaxLength(100);
            e.Property(x => x.AttachmentPath).HasMaxLength(400);
            e.Property(x => x.RequestedById).HasMaxLength(450);
            e.Property(x => x.DecidedById).HasMaxLength(450);
            e.Property(x => x.DecidedByName).HasMaxLength(200);
            e.Property(x => x.DecisionNote).HasMaxLength(1000);
            e.Property(x => x.Days).HasPrecision(6, 2);
            e.HasOne(x => x.Employee).WithMany()
                .HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LeaveType).WithMany()
                .HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<LeaveBalance>(e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.LeaveTypeId, x.Year }).IsUnique();
            foreach (var p in new[] { "Entitled", "CarriedForward", "Taken", "Pending" })
                e.Property(p).HasPrecision(6, 2);
            e.HasOne(x => x.Employee).WithMany()
                .HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.LeaveType).WithMany()
                .HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}
