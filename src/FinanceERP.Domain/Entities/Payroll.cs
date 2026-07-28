using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

/// <summary>
/// Catalog entry for a salary component — an allowance (adds to gross) or a
/// deduction (subtracts from gross). Employees pick these up through their
/// <see cref="SalaryStructure"/>; the payroll run copies the resolved amounts
/// onto the payslip so historic slips never move when the catalog changes.
/// </summary>
public class PayComponent : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public PayComponentKind Kind { get; set; }
    /// <summary>Default calculation basis; a structure line may override it.</summary>
    public PayComponentCalc Calc { get; set; } = PayComponentCalc.FixedAmount;
    /// <summary>Fixed amount, or percent of basic when <see cref="Calc"/> is PercentOfBasic.</summary>
    public decimal DefaultValue { get; set; }
    /// <summary>
    /// Ledger head. Allowances: expense account (defaults to Salary Expense when null).
    /// Deductions: liability account the withheld money is parked in (e.g. Taxes Payable).
    /// </summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>System components (Basic, advance recovery, absence) cannot be deleted.</summary>
    public bool IsSystem { get; set; }
    public int SortOrder { get; set; }
    public string? Description { get; set; }
}

/// <summary>An employee's salary definition. Superseded rather than edited: a new
/// structure with a later EffectiveFrom deactivates the previous one.</summary>
public class SalaryStructure : AuditableEntity
{
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public List<SalaryStructureLine> Lines { get; set; } = [];

    public decimal MonthlyGross(IEnumerable<SalaryStructureLine>? lines = null)
    {
        var src = lines ?? Lines;
        return BasicSalary + src.Where(l => l.Kind == PayComponentKind.Allowance)
            .Sum(l => l.Resolve(BasicSalary));
    }
}

public class SalaryStructureLine : BaseEntity
{
    public int SalaryStructureId { get; set; }
    public SalaryStructure SalaryStructure { get; set; } = null!;
    public int PayComponentId { get; set; }
    public PayComponent PayComponent { get; set; } = null!;
    /// <summary>Denormalised from the component so structures survive catalog edits.</summary>
    public PayComponentKind Kind { get; set; }
    public PayComponentCalc Calc { get; set; } = PayComponentCalc.FixedAmount;
    public decimal Value { get; set; }

    public decimal Resolve(decimal basic) => Math.Round(
        Calc == PayComponentCalc.PercentOfBasic ? basic * Value / 100m : Value, 2);
}

/// <summary>One month's payroll: draft → pending approval → approved → paid.
/// Posting happens once, on pay, as a single balanced voucher.</summary>
public class PayrollRun : AuditableEntity
{
    public string RunNo { get; set; } = string.Empty;
    /// <summary>Always the first of the payroll month.</summary>
    public DateOnly PeriodMonth { get; set; }
    public DateOnly PayDate { get; set; }
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;
    /// <summary>Cash/bank account net pay is disbursed from (chosen at pay time).</summary>
    public int? PayFromAccountId { get; set; }
    public int? VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNet { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? Notes { get; set; }
    public List<Payslip> Payslips { get; set; } = [];
}

public class Payslip : BaseEntity
{
    public int PayrollRunId { get; set; }
    public PayrollRun PayrollRun { get; set; } = null!;
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? EmployeeCode { get; set; }
    public int? SalaryStructureId { get; set; }
    public decimal BasicSalary { get; set; }
    /// <summary>Basic after pro-rating for unpaid absence.</summary>
    public decimal EarnedBasic { get; set; }
    public int WorkingDays { get; set; } = 30;
    public decimal AbsentDays { get; set; }
    public decimal TotalAllowances { get; set; }
    public decimal GrossPay { get; set; }
    /// <summary>Component deductions (tax, absence, manual) — excludes advance recovery.</summary>
    public decimal TotalDeductions { get; set; }
    /// <summary>Advance instalments recovered from this month's salary.</summary>
    public decimal AdvanceDeduction { get; set; }
    public decimal NetPay { get; set; }
    public string? Notes { get; set; }
    public List<PayslipLine> Lines { get; set; } = [];
}

public class PayslipLine : BaseEntity
{
    public int PayslipId { get; set; }
    public Payslip Payslip { get; set; } = null!;
    public int? PayComponentId { get; set; }
    public PayComponent? PayComponent { get; set; }
    /// <summary>Snapshot of the component name at run time.</summary>
    public string Name { get; set; } = string.Empty;
    public PayComponentKind Kind { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Ledger head resolved at run time (expense for allowances, liability for deductions).</summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
    /// <summary>Set when this deduction recovers an employee advance instalment.</summary>
    public int? AdvanceInstallmentId { get; set; }
    public AdvanceInstallment? AdvanceInstallment { get; set; }
    /// <summary>Human-readable origin, e.g. "Advance ADV-2026-00003 #2".</summary>
    public string? SourceRef { get; set; }
    public int LineNo { get; set; }
}
