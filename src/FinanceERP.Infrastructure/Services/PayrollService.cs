using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Domain.Security;
using FinanceERP.Infrastructure.Identity;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class PayrollService(
    AppDbContext db,
    ICurrentUserService currentUser,
    IVoucherService voucherService,
    IAdvanceService advances,
    INotificationService notifications) : IPayrollService
{
    private const string SalaryExpenseCode = "5140";
    private const string SalariesPayableCode = "2400";

    // ---------------------------------------------------------------- components

    public Task<List<PayComponent>> GetComponentsAsync(bool activeOnly = true)
    {
        var q = db.PayComponents.Include(c => c.Account).AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(c => c.IsActive);
        return q.OrderBy(c => c.Kind).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
    }

    public async Task<PayComponent> SaveComponentAsync(PayComponent component)
    {
        if (string.IsNullOrWhiteSpace(component.Name))
            throw new InvalidOperationException("Component name is required.");
        if (component.Calc == PayComponentCalc.PercentOfBasic && component.DefaultValue is < 0 or > 100)
            throw new InvalidOperationException("Percent of basic must be between 0 and 100.");

        if (component.Id == 0)
        {
            db.PayComponents.Add(component);
        }
        else
        {
            var e = await db.PayComponents.FirstAsync(c => c.Id == component.Id);
            e.Name = component.Name;
            e.Code = component.Code;
            e.Kind = component.Kind;
            e.Calc = component.Calc;
            e.DefaultValue = component.DefaultValue;
            e.AccountId = component.AccountId;
            e.IsActive = component.IsActive;
            e.SortOrder = component.SortOrder;
            e.Description = component.Description;
            component = e;
        }
        await db.SaveChangesAsync();
        return component;
    }

    public async Task DeleteComponentAsync(int id)
    {
        var c = await db.PayComponents.FirstAsync(x => x.Id == id);
        if (c.IsSystem) throw new InvalidOperationException("System components cannot be deleted — deactivate it instead.");
        if (await db.SalaryStructureLines.AnyAsync(l => l.PayComponentId == id))
            throw new InvalidOperationException("This component is used by a salary structure — deactivate it instead.");
        db.PayComponents.Remove(c);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- structures

    public Task<List<SalaryStructure>> GetStructuresAsync(bool activeOnly = true)
    {
        var q = db.SalaryStructures.Include(s => s.Lines).ThenInclude(l => l.PayComponent)
            .AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(s => s.IsActive);
        return q.OrderBy(s => s.EmployeeName).ToListAsync();
    }

    public Task<SalaryStructure?> GetStructureAsync(int id) =>
        db.SalaryStructures.Include(s => s.Lines).ThenInclude(l => l.PayComponent)
            .FirstOrDefaultAsync(s => s.Id == id);

    public Task<SalaryStructure?> GetActiveStructureForAsync(string employeeId, DateOnly asOf) =>
        db.SalaryStructures.Include(s => s.Lines).ThenInclude(l => l.PayComponent)
            .Where(s => s.EmployeeId == employeeId && s.IsActive && s.EffectiveFrom <= asOf)
            .OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync();

    public async Task<SalaryStructure> SaveStructureAsync(SalaryStructure structure)
    {
        if (string.IsNullOrWhiteSpace(structure.EmployeeId))
            throw new InvalidOperationException("Select an employee.");
        if (structure.BasicSalary <= 0)
            throw new InvalidOperationException("Basic salary must be positive.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == structure.EmployeeId)
                   ?? throw new InvalidOperationException("Employee not found.");
        structure.EmployeeName = user.FullName;

        foreach (var line in structure.Lines)
        {
            var comp = await db.PayComponents.FirstOrDefaultAsync(c => c.Id == line.PayComponentId)
                       ?? throw new InvalidOperationException("Unknown salary component.");
            line.Kind = comp.Kind;
            if (line.Calc == PayComponentCalc.PercentOfBasic && line.Value is < 0 or > 100)
                throw new InvalidOperationException($"{comp.Name}: percent of basic must be between 0 and 100.");
            if (line.Value < 0) throw new InvalidOperationException($"{comp.Name}: value cannot be negative.");
        }

        if (structure.Id == 0)
        {
            // A new structure supersedes whatever the employee was on before.
            var previous = await db.SalaryStructures
                .Where(s => s.EmployeeId == structure.EmployeeId && s.IsActive).ToListAsync();
            foreach (var p in previous) p.IsActive = false;
            db.SalaryStructures.Add(structure);
        }
        else
        {
            var e = await db.SalaryStructures.Include(s => s.Lines).FirstAsync(s => s.Id == structure.Id);
            e.BasicSalary = structure.BasicSalary;
            e.EffectiveFrom = structure.EffectiveFrom;
            e.IsActive = structure.IsActive;
            e.Notes = structure.Notes;
            db.SalaryStructureLines.RemoveRange(e.Lines);
            e.Lines = structure.Lines.Select(l => new SalaryStructureLine
            {
                PayComponentId = l.PayComponentId, Kind = l.Kind, Calc = l.Calc, Value = l.Value
            }).ToList();
            structure = e;
        }
        await db.SaveChangesAsync();
        return structure;
    }

    public async Task DeleteStructureAsync(int id)
    {
        var s = await db.SalaryStructures.FirstAsync(x => x.Id == id);
        db.SalaryStructures.Remove(s); // soft delete
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- runs

    public async Task<PagedResult<PayrollRun>> ListRunsAsync(ReportFilter f)
    {
        var q = db.PayrollRuns.AsNoTracking().AsQueryable();
        if (f.From is not null) q = q.Where(r => r.PeriodMonth >= f.From);
        if (f.To is not null) q = q.Where(r => r.PeriodMonth <= f.To);
        if (f.DepartmentId is not null) q = q.Where(r => r.DepartmentId == f.DepartmentId);
        if (f.ProjectId is not null) q = q.Where(r => r.ProjectId == f.ProjectId);
        if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(r => r.RunNo.Contains(f.Search));

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.PeriodMonth).ThenByDescending(r => r.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        return new PagedResult<PayrollRun>(items, total);
    }

    public Task<PayrollRun?> GetRunAsync(int id) =>
        db.PayrollRuns
            .Include(r => r.Department)
            .Include(r => r.Project)
            .Include(r => r.Voucher)
            .Include(r => r.Payslips.OrderBy(p => p.EmployeeName))
                .ThenInclude(p => p.Lines.OrderBy(l => l.LineNo))
                    .ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(r => r.Id == id);

    public async Task<PayrollRun> CreateRunAsync(DateOnly periodMonth, DateOnly payDate, int? departmentId, int? projectId)
    {
        var month = new DateOnly(periodMonth.Year, periodMonth.Month, 1);
        if (await db.PayrollRuns.AnyAsync(r => r.PeriodMonth == month && r.Status != PayrollRunStatus.Cancelled))
            throw new InvalidOperationException($"A payroll run for {month:MMMM yyyy} already exists.");

        var run = new PayrollRun
        {
            RunNo = $"PAY-{month:yyyy-MM}",
            PeriodMonth = month,
            PayDate = payDate,
            DepartmentId = departmentId,
            ProjectId = projectId,
            Status = PayrollRunStatus.Draft
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public async Task<List<SalaryStructure>> GetEligibleEmployeesAsync(int runId)
    {
        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        var periodEnd = run.PeriodMonth.AddMonths(1).AddDays(-1);
        var already = await db.Payslips.Where(p => p.PayrollRunId == runId).Select(p => p.EmployeeId).ToListAsync();

        var structures = await db.SalaryStructures
            .Include(s => s.Lines).ThenInclude(l => l.PayComponent)
            .Where(s => s.IsActive && s.EffectiveFrom <= periodEnd && !already.Contains(s.EmployeeId))
            .AsNoTracking().ToListAsync();

        if (run.DepartmentId is null) return structures.OrderBy(s => s.EmployeeName).ToList();

        var inDept = await db.Users.Where(u => u.DepartmentId == run.DepartmentId).Select(u => u.Id).ToListAsync();
        return structures.Where(s => inDept.Contains(s.EmployeeId)).OrderBy(s => s.EmployeeName).ToList();
    }

    /// <summary>
    /// Rebuilds the run's payslips from scratch. Everything is snapshotted onto the
    /// payslip (names, accounts, amounts) so an approved run can never shift under a
    /// later catalog or structure edit.
    /// </summary>
    public async Task<PayrollRun> GenerateAsync(int runId, IEnumerable<string>? employeeIds = null,
        IReadOnlyDictionary<string, PayslipInputDto>? inputs = null)
    {
        var run = await db.PayrollRuns.Include(r => r.Payslips).ThenInclude(p => p.Lines)
            .FirstAsync(r => r.Id == runId);
        if (run.Status is not (PayrollRunStatus.Draft or PayrollRunStatus.PendingApproval))
            throw new InvalidOperationException("Only a draft run can be regenerated.");

        var periodEnd = run.PeriodMonth.AddMonths(1).AddDays(-1);
        var salaryExpense = await AccountIdAsync(SalaryExpenseCode);
        var salariesPayable = await AccountIdAsync(SalariesPayableCode);

        // Which employees this run covers: an explicit list, or everyone already on it,
        // falling back to every eligible employee for a freshly created run.
        var targets = employeeIds?.Distinct().ToList()
                      ?? (run.Payslips.Count > 0
                          ? run.Payslips.Select(p => p.EmployeeId).Distinct().ToList()
                          : (await GetEligibleEmployeesAsync(runId)).Select(s => s.EmployeeId).ToList());

        // Preserve per-employee inputs from the previous generation unless overridden.
        var previousInputs = run.Payslips.ToDictionary(p => p.EmployeeId, p => p);

        db.PayslipLines.RemoveRange(run.Payslips.SelectMany(p => p.Lines));
        db.Payslips.RemoveRange(run.Payslips);
        run.Payslips = [];

        var users = await db.Users.Where(u => targets.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u);

        foreach (var employeeId in targets)
        {
            var structure = await GetActiveStructureForAsync(employeeId, periodEnd);
            if (structure is null) continue; // no salary defined — silently skipped

            users.TryGetValue(employeeId, out var user);
            previousInputs.TryGetValue(employeeId, out var prior);

            var input = inputs is not null && inputs.TryGetValue(employeeId, out var supplied)
                ? supplied
                : new PayslipInputDto
                {
                    EmployeeId = employeeId,
                    AbsentDays = prior?.AbsentDays ?? 0,
                    WorkingDays = prior?.WorkingDays ?? 30,
                    Notes = prior?.Notes
                };

            var slip = await BuildPayslipAsync(structure, input, user, periodEnd, salaryExpense, salariesPayable);
            run.Payslips.Add(slip);
        }

        RecomputeRunTotals(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>
    /// The salary arithmetic, in one place:
    /// earned basic (pro-rated for unpaid absence) + allowances (also pro-rated)
    /// − component deductions − manual deductions − due advance instalments = net pay.
    /// </summary>
    private async Task<Payslip> BuildPayslipAsync(SalaryStructure structure, PayslipInputDto input,
        ApplicationUser? user, DateOnly periodEnd, int salaryExpense, int salariesPayable)
    {
        var workingDays = input.WorkingDays > 0 ? input.WorkingDays : 30;
        var absent = Math.Clamp(input.AbsentDays, 0, workingDays);
        var attendanceFactor = (workingDays - absent) / workingDays;

        var slip = new Payslip
        {
            EmployeeId = structure.EmployeeId,
            EmployeeName = structure.EmployeeName,
            EmployeeCode = user?.EmployeeCode,
            SalaryStructureId = structure.Id,
            BasicSalary = structure.BasicSalary,
            EarnedBasic = Math.Round(structure.BasicSalary * attendanceFactor, 2),
            WorkingDays = workingDays,
            AbsentDays = absent,
            Notes = input.Notes
        };

        var lineNo = 1;
        slip.Lines.Add(new PayslipLine
        {
            Name = "Basic Salary", Kind = PayComponentKind.Allowance, Amount = slip.EarnedBasic,
            AccountId = salaryExpense, LineNo = lineNo++,
            SourceRef = absent > 0 ? $"Pro-rated: {workingDays - absent}/{workingDays} days" : null
        });

        // Percent-of-basic components resolve against full basic, then pro-rate with attendance,
        // so an absence reduces basic and its allowances by the same proportion.
        foreach (var line in structure.Lines.OrderBy(l => l.PayComponent.SortOrder).ThenBy(l => l.PayComponent.Name))
        {
            var amount = Math.Round(line.Resolve(structure.BasicSalary) * attendanceFactor, 2);
            if (amount <= 0) continue;

            slip.Lines.Add(new PayslipLine
            {
                PayComponentId = line.PayComponentId,
                Name = line.PayComponent.Name,
                Kind = line.Kind,
                Amount = amount,
                AccountId = line.PayComponent.AccountId
                            ?? (line.Kind == PayComponentKind.Allowance ? salaryExpense : salariesPayable),
                LineNo = lineNo++,
                SourceRef = line.Calc == PayComponentCalc.PercentOfBasic ? $"{line.Value:0.##}% of basic" : null
            });
        }

        foreach (var manual in input.ManualDeductions.Where(m => m.Amount > 0))
            slip.Lines.Add(new PayslipLine
            {
                Name = manual.Label, Kind = PayComponentKind.Deduction, Amount = Math.Round(manual.Amount, 2),
                AccountId = manual.AccountId ?? salariesPayable, LineNo = lineNo++, SourceRef = "Manual deduction"
            });

        slip.TotalAllowances = slip.Lines.Where(l => l.Kind == PayComponentKind.Allowance).Sum(l => l.Amount)
                               - slip.EarnedBasic;
        slip.GrossPay = slip.EarnedBasic + slip.TotalAllowances;
        slip.TotalDeductions = slip.Lines.Where(l => l.Kind == PayComponentKind.Deduction).Sum(l => l.Amount);

        // Advance recovery, capped at what's actually left after other deductions so a
        // payslip can never go negative; the shortfall simply rolls to next month.
        if (input.DeductAdvances)
        {
            var advAccount = await advances.GetAdvanceAccountAsync(structure.EmployeeName);
            var room = slip.GrossPay - slip.TotalDeductions;

            foreach (var inst in await advances.GetDueInstallmentsAsync(structure.EmployeeId, periodEnd))
            {
                if (room <= 0) break;
                var outstanding = inst.Amount - inst.PaidAmount;
                var take = Math.Round(Math.Min(outstanding, room), 2);
                if (take <= 0) continue;

                slip.Lines.Add(new PayslipLine
                {
                    Name = $"Advance recovery — {inst.EmployeeAdvance.AdvanceNo}",
                    Kind = PayComponentKind.Deduction,
                    Amount = take,
                    AccountId = advAccount.Id,
                    AdvanceInstallmentId = inst.Id,
                    SourceRef = $"Instalment #{inst.Number} due {inst.DueDate:yyyy-MM-dd}"
                              + (take < outstanding ? $" (partial of {outstanding:N2})" : ""),
                    LineNo = lineNo++
                });
                slip.AdvanceDeduction += take;
                room -= take;
            }
        }

        slip.NetPay = slip.GrossPay - slip.TotalDeductions - slip.AdvanceDeduction;
        return slip;
    }

    private static void RecomputeRunTotals(PayrollRun run)
    {
        run.TotalGross = run.Payslips.Sum(p => p.GrossPay);
        run.TotalDeductions = run.Payslips.Sum(p => p.TotalDeductions + p.AdvanceDeduction);
        run.TotalNet = run.Payslips.Sum(p => p.NetPay);
    }

    public async Task SubmitRunAsync(int runId)
    {
        var run = await db.PayrollRuns.Include(r => r.Payslips).FirstAsync(r => r.Id == runId);
        if (run.Status != PayrollRunStatus.Draft) throw new InvalidOperationException("Only a draft run can be submitted.");
        if (run.Payslips.Count == 0) throw new InvalidOperationException("Generate payslips before submitting.");

        run.Status = PayrollRunStatus.PendingApproval;
        await db.SaveChangesAsync();
        await notifications.NotifyRoleAsync(AppRoles.Admin, $"Payroll approval: {run.RunNo}",
            $"{run.Payslips.Count} employees — net {run.TotalNet:N2}",
            NotificationType.ApprovalRequest, $"/payroll/{run.Id}");
    }

    public async Task ApproveRunAsync(int runId, string? comment)
    {
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == runId);
        if (run.Status != PayrollRunStatus.PendingApproval) throw new InvalidOperationException("Run is not pending approval.");

        run.Status = PayrollRunStatus.Approved;
        run.ApprovedBy = currentUser.UserName;
        run.ApprovedAtUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(comment)) run.Notes = comment;
        await db.SaveChangesAsync();

        await notifications.NotifyRoleAsync(AppRoles.Accountant, $"Pay payroll: {run.RunNo}",
            $"Approved — net {run.TotalNet:N2} to disburse", NotificationType.ApprovalRequest, $"/payroll/{run.Id}");
    }

    public async Task RejectRunAsync(int runId, string? comment)
    {
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == runId);
        if (run.Status != PayrollRunStatus.PendingApproval) throw new InvalidOperationException("Run is not pending approval.");
        run.Status = PayrollRunStatus.Draft;
        run.Notes = comment;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Single balanced voucher for the whole run, aggregated by ledger head:
    ///   Dr salary expense heads       (gross earnings)
    ///   Cr deduction liability heads  (tax, EOBI, manual, ...)
    ///   Cr employee advance accounts  (instalments recovered)
    ///   Cr cash/bank                  (net pay actually disbursed)
    /// </summary>
    public async Task<Voucher> PayRunAsync(int runId, int payFromAccountId, string? comment)
    {
        var run = await db.PayrollRuns.Include(r => r.Payslips).ThenInclude(p => p.Lines)
            .FirstAsync(r => r.Id == runId);
        if (run.Status != PayrollRunStatus.Approved)
            throw new InvalidOperationException("Payroll must be approved before payment.");
        if (run.Payslips.Count == 0) throw new InvalidOperationException("This run has no payslips.");

        RecomputeRunTotals(run);
        if (run.TotalNet <= 0) throw new InvalidOperationException("Net pay for this run is zero — nothing to disburse.");

        var salaryExpense = await AccountIdAsync(SalaryExpenseCode);
        var salariesPayable = await AccountIdAsync(SalariesPayableCode);
        var period = $"{run.PeriodMonth:MMMM yyyy}";

        var debits = new Dictionary<int, decimal>();
        var credits = new Dictionary<int, decimal>();
        void Add(Dictionary<int, decimal> bucket, int accountId, decimal amount)
        {
            if (amount <= 0) return;
            bucket[accountId] = bucket.GetValueOrDefault(accountId) + amount;
        }

        foreach (var line in run.Payslips.SelectMany(p => p.Lines))
        {
            var account = line.AccountId
                          ?? (line.Kind == PayComponentKind.Allowance ? salaryExpense : salariesPayable);
            if (line.Kind == PayComponentKind.Allowance) Add(debits, account, line.Amount);
            else Add(credits, account, line.Amount);
        }
        Add(credits, payFromAccountId, run.TotalNet);

        var accountNames = await db.Accounts
            .Where(a => debits.Keys.Contains(a.Id) || credits.Keys.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name);

        var voucherLines = debits
            .Select(kv => (AccountId: kv.Key, Debit: kv.Value, Credit: 0m,
                Description: (string?)$"Payroll {period} — {accountNames.GetValueOrDefault(kv.Key)}"))
            .Concat(credits.Select(kv => (AccountId: kv.Key, Debit: 0m, Credit: kv.Value,
                Description: (string?)(kv.Key == payFromAccountId
                    ? $"Payroll {period} — net pay to {run.Payslips.Count} employee(s)"
                    : $"Payroll {period} — {accountNames.GetValueOrDefault(kv.Key)}"))))
            .ToList();

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.BankPayment, run.PayDate,
            $"Payroll {run.RunNo} — {period}: gross {run.TotalGross:N2}, net {run.TotalNet:N2}",
            "PayrollRun", run.Id, voucherLines);

        foreach (var vl in voucher.Lines)
        {
            vl.ProjectId = run.ProjectId;
            vl.DepartmentId = run.DepartmentId;
        }

        run.Status = PayrollRunStatus.Paid;
        run.VoucherId = voucher.Id;
        run.PayFromAccountId = payFromAccountId;
        if (!string.IsNullOrWhiteSpace(comment)) run.Notes = comment;
        await db.SaveChangesAsync();

        // The voucher already credited the advance accounts, so this only moves
        // instalment state forward — see AdvanceService.ApplyPayrollDeductionAsync.
        foreach (var line in run.Payslips.SelectMany(p => p.Lines).Where(l => l.AdvanceInstallmentId is not null))
            await advances.ApplyPayrollDeductionAsync(line.AdvanceInstallmentId!.Value, line.Amount,
                voucher.Id, run.PayDate);

        foreach (var slip in run.Payslips)
            await notifications.NotifyAsync(slip.EmployeeId, $"Salary paid — {period}",
                $"Net {slip.NetPay:N2}" + (slip.AdvanceDeduction > 0
                    ? $" (after {slip.AdvanceDeduction:N2} advance recovery)" : ""),
                NotificationType.Info, $"/my-payslips");

        return voucher;
    }

    public async Task CancelRunAsync(int runId, string? reason)
    {
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == runId);
        if (run.Status == PayrollRunStatus.Paid)
            throw new InvalidOperationException("A paid run cannot be cancelled — void its voucher instead.");
        run.Status = PayrollRunStatus.Cancelled;
        run.Notes = reason;
        await db.SaveChangesAsync();
    }

    public async Task DeleteRunAsync(int runId)
    {
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == runId);
        if (run.Status == PayrollRunStatus.Paid)
            throw new InvalidOperationException("A paid run cannot be deleted — void its voucher instead.");
        db.PayrollRuns.Remove(run); // soft delete
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- payslips

    public Task<Payslip?> GetPayslipAsync(int id) =>
        db.Payslips
            .Include(p => p.PayrollRun)
            .Include(p => p.Lines.OrderBy(l => l.LineNo)).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(p => p.Id == id);

    public Task<List<Payslip>> GetPayslipsForEmployeeAsync(string employeeId, int max = 60) =>
        db.Payslips
            .Include(p => p.PayrollRun)
            .Include(p => p.Lines.OrderBy(l => l.LineNo))
            .Where(p => p.EmployeeId == employeeId && p.PayrollRun.Status == PayrollRunStatus.Paid)
            .OrderByDescending(p => p.PayrollRun.PeriodMonth)
            .Take(max).AsNoTracking().ToListAsync();

    private async Task<int> AccountIdAsync(string code) =>
        (await db.Accounts.FirstOrDefaultAsync(a => a.Code == code)
         ?? throw new InvalidOperationException($"Chart of accounts is missing account {code}."))
        .Id;
}
