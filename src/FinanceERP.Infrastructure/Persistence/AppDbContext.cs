using System.Text.Json;
using ErpPlatform.Shared.Kernel;
using FinanceERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Persistence;

/// <summary>
/// The Finance module's database (<c>erp_accounts</c>). Identity no longer lives
/// here — users, roles and permissions are in the shared identity database, and
/// this context refers to people only by their Identity user id string. See
/// <see cref="EmployeeProfile"/> for the accounts-side mirror.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUserService currentUser)
    : DbContext(options)
{
    public DbSet<EmployeeProfile> EmployeeProfiles => Set<EmployeeProfile>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherLine> VoucherLines => Set<VoucherLine>();
    public DbSet<VoucherSequence> VoucherSequences => Set<VoucherSequence>();
    public DbSet<ThirdParty> ThirdParties => Set<ThirdParty>();
    public DbSet<PaymentRequest> PaymentRequests => Set<PaymentRequest>();
    public DbSet<PaymentRequestLine> PaymentRequestLines => Set<PaymentRequestLine>();
    public DbSet<RequestApproval> RequestApprovals => Set<RequestApproval>();
    public DbSet<EmployeeAdvance> EmployeeAdvances => Set<EmployeeAdvance>();
    public DbSet<AdvanceInstallment> AdvanceInstallments => Set<AdvanceInstallment>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanInstallment> LoanInstallments => Set<LoanInstallment>();
    public DbSet<Investment> Investments => Set<Investment>();
    public DbSet<InvestmentTransaction> InvestmentTransactions => Set<InvestmentTransaction>();
    public DbSet<PettyCashAssignment> PettyCashAssignments => Set<PettyCashAssignment>();
    public DbSet<UtilityLocation> UtilityLocations => Set<UtilityLocation>();
    public DbSet<UtilityConnection> UtilityConnections => Set<UtilityConnection>();
    public DbSet<UtilityBill> UtilityBills => Set<UtilityBill>();
    public DbSet<PayComponent> PayComponents => Set<PayComponent>();
    public DbSet<SalaryStructure> SalaryStructures => Set<SalaryStructure>();
    public DbSet<SalaryStructureLine> SalaryStructureLines => Set<SalaryStructureLine>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<PayslipLine> PayslipLines => Set<PayslipLine>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Account>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.Parent).WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Voucher>(e =>
        {
            e.HasIndex(x => x.VoucherNo).IsUnique();
            e.Property(x => x.VoucherNo).HasMaxLength(32);
            e.Property(x => x.TotalDebit).HasPrecision(18, 2);
            e.Property(x => x.TotalCredit).HasPrecision(18, 2);
            e.HasIndex(x => x.Date);
            e.HasIndex(x => new { x.Type, x.Status });
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<VoucherLine>(e =>
        {
            e.Property(x => x.Debit).HasPrecision(18, 2);
            e.Property(x => x.Credit).HasPrecision(18, 2);
            e.HasOne(x => x.Voucher).WithMany(x => x.Lines)
                .HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account).WithMany()
                .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AccountId);
            e.HasQueryFilter(x => !x.Voucher.IsDeleted);
        });

        b.Entity<VoucherSequence>(e => e.HasIndex(x => new { x.Type, x.Year }).IsUnique());

        b.Entity<ThirdParty>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<PaymentRequest>(e =>
        {
            e.HasIndex(x => x.RequestNo).IsUnique();
            e.Property(x => x.RequestNo).HasMaxLength(32);
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.Property(x => x.ClearedDifference).HasPrecision(18, 2);
            e.HasIndex(x => new { x.RequesterId, x.Status });
            e.HasOne(x => x.Voucher).WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SettlementVoucher).WithMany().HasForeignKey(x => x.SettlementVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<PaymentRequestLine>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.PaymentRequest.IsDeleted);
        });

        b.Entity<RequestApproval>(e => e.HasQueryFilter(x => !x.PaymentRequest.IsDeleted));

        b.Entity<EmployeeAdvance>(e =>
        {
            e.HasIndex(x => x.AdvanceNo).IsUnique();
            e.Property(x => x.AdvanceNo).HasMaxLength(32);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.MonthlyDeduction).HasPrecision(18, 2);
            e.Property(x => x.RepaidAmount).HasPrecision(18, 2);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<AdvanceInstallment>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.PaidAmount).HasPrecision(18, 2);
            e.HasQueryFilter(x => !x.EmployeeAdvance.IsDeleted);
        });

        b.Entity<Loan>(e =>
        {
            e.HasIndex(x => x.LoanNo).IsUnique();
            e.Property(x => x.LoanNo).HasMaxLength(32);
            e.Property(x => x.Principal).HasPrecision(18, 2);
            e.Property(x => x.InterestRatePercent).HasPrecision(8, 4);
            e.Property(x => x.RepaidAmount).HasPrecision(18, 2);
            e.HasOne(x => x.ThirdParty).WithMany().HasForeignKey(x => x.ThirdPartyId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<LoanInstallment>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.InterestPortion).HasPrecision(18, 2);
            e.Property(x => x.PaidAmount).HasPrecision(18, 2);
            e.HasQueryFilter(x => !x.Loan.IsDeleted);
        });

        b.Entity<Investment>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.ExpectedRoiPercent).HasPrecision(8, 4);
            e.Property(x => x.TotalProfit).HasPrecision(18, 2);
            e.Property(x => x.TotalWithdrawn).HasPrecision(18, 2);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<InvestmentTransaction>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasQueryFilter(x => !x.Investment.IsDeleted);
        });

        b.Entity<PettyCashAssignment>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<UtilityLocation>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(150);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<UtilityConnection>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.ConsumerNumber).HasMaxLength(60);
            e.HasOne(x => x.Location).WithMany(x => x.Connections)
                .HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ExpenseAccount).WithMany()
                .HasForeignKey(x => x.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Location.IsDeleted);
        });

        b.Entity<UtilityBill>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasOne(x => x.Connection).WithMany(x => x.Bills)
                .HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ConnectionId, x.BillMonth });
            e.HasIndex(x => x.DueDate);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Connection.IsDeleted && !x.Connection.Location.IsDeleted);
        });

        b.Entity<PayComponent>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.DefaultValue).HasPrecision(18, 2);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<SalaryStructure>(e =>
        {
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.BasicSalary).HasPrecision(18, 2);
            e.HasIndex(x => new { x.EmployeeId, x.IsActive });
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<SalaryStructureLine>(e =>
        {
            e.Property(x => x.Value).HasPrecision(18, 2);
            e.HasOne(x => x.SalaryStructure).WithMany(x => x.Lines)
                .HasForeignKey(x => x.SalaryStructureId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PayComponent).WithMany()
                .HasForeignKey(x => x.PayComponentId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.SalaryStructure.IsDeleted);
        });

        b.Entity<PayrollRun>(e =>
        {
            e.HasIndex(x => x.RunNo).IsUnique();
            e.Property(x => x.RunNo).HasMaxLength(32);
            e.Property(x => x.TotalGross).HasPrecision(18, 2);
            e.Property(x => x.TotalDeductions).HasPrecision(18, 2);
            e.Property(x => x.TotalNet).HasPrecision(18, 2);
            e.HasIndex(x => x.PeriodMonth);
            e.HasOne(x => x.Voucher).WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Payslip>(e =>
        {
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.BasicSalary).HasPrecision(18, 2);
            e.Property(x => x.EarnedBasic).HasPrecision(18, 2);
            e.Property(x => x.AbsentDays).HasPrecision(8, 2);
            e.Property(x => x.TotalAllowances).HasPrecision(18, 2);
            e.Property(x => x.GrossPay).HasPrecision(18, 2);
            e.Property(x => x.TotalDeductions).HasPrecision(18, 2);
            e.Property(x => x.AdvanceDeduction).HasPrecision(18, 2);
            e.Property(x => x.NetPay).HasPrecision(18, 2);
            e.HasIndex(x => new { x.PayrollRunId, x.EmployeeId });
            e.HasOne(x => x.PayrollRun).WithMany(x => x.Payslips)
                .HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.PayrollRun.IsDeleted);
        });

        b.Entity<PayslipLine>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasOne(x => x.Payslip).WithMany(x => x.Lines)
                .HasForeignKey(x => x.PayslipId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PayComponent).WithMany()
                .HasForeignKey(x => x.PayComponentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Account).WithMany()
                .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AdvanceInstallment).WithMany()
                .HasForeignKey(x => x.AdvanceInstallmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.Payslip.PayrollRun.IsDeleted);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.EntityName, x.EntityId });
        });

        b.Entity<Notification>(e => e.HasIndex(x => new { x.UserId, x.IsRead }));
        b.Entity<AppSetting>(e => e.HasIndex(x => x.Key).IsUnique());

        b.Entity<EmployeeProfile>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.EmployeeCode).HasMaxLength(50);
            e.HasOne(x => x.Department).WithMany()
                .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LedgerAccount).WithMany()
                .HasForeignKey(x => x.LedgerAccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var user = currentUser.UserName;
        var audits = new List<AuditLog>();

        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            if (entry.Entity is BaseEntity be)
            {
                if (entry.State == EntityState.Added) { be.CreatedAtUtc = now; be.CreatedBy = user; }
                else if (entry.State == EntityState.Modified) { be.ModifiedAtUtc = now; be.ModifiedBy = user; }
            }

            // Soft delete: convert hard deletes into flag updates.
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDelete sd)
            {
                entry.State = EntityState.Modified;
                sd.IsDeleted = true;
                sd.DeletedAtUtc = now;
                sd.DeletedBy = user;
            }

            if (entry.Entity is AuditLog or Notification) continue;

            var action = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Deleted => "Deleted",
                _ => entry.Entity is ISoftDelete { IsDeleted: true } ? "SoftDeleted" : "Modified"
            };

            var oldVals = entry.State == EntityState.Added ? null :
                JsonSerializer.Serialize(entry.Properties.Where(p => p.IsModified || entry.State == EntityState.Deleted)
                    .ToDictionary(p => p.Metadata.Name, p => p.OriginalValue?.ToString()));
            var newVals = entry.State == EntityState.Deleted ? null :
                JsonSerializer.Serialize(entry.Properties.Where(p => p.IsModified || entry.State == EntityState.Added)
                    .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue?.ToString()));

            audits.Add(new AuditLog
            {
                UserId = currentUser.UserId,
                UserName = user,
                IpAddress = currentUser.IpAddress,
                Browser = currentUser.Browser,
                Action = action,
                EntityName = entry.Metadata.ClrType.Name,
                EntityId = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString(),
                OldValues = oldVals,
                NewValues = newVals,
                TimestampUtc = now
            });
        }

        var result = await base.SaveChangesAsync(ct);

        if (audits.Count > 0)
        {
            AuditLogs.AddRange(audits);
            await base.SaveChangesAsync(ct);
        }

        return result;
    }
}
