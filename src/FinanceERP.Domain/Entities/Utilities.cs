using FinanceERP.Domain.Common;

namespace FinanceERP.Domain.Entities;

public enum UtilityType
{
    Electricity = 1,
    Gas = 2,
    Water = 3,
    Internet = 4,
    Telephone = 5,
    /// <summary>Society/maintenance dues (e.g. DHA general bill).</summary>
    SocietyDues = 6,
    SchoolFee = 7,
    Other = 99
}

/// <summary>A site whose utilities are tracked (Islamabad Home, Karachi Office, ...).</summary>
public class UtilityLocation : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public List<UtilityConnection> Connections { get; set; } = [];
}

/// <summary>
/// One billable connection at a location: an electricity meter, a gas meter,
/// an internet line, a school fee account... identified by its consumer number.
/// </summary>
public class UtilityConnection : AuditableEntity
{
    public int LocationId { get; set; }
    public UtilityLocation Location { get; set; } = null!;
    public UtilityType Type { get; set; }
    /// <summary>Friendly name, e.g. "Electricity — Upper Portion".</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Meter / consumer / account number printed on the bill.</summary>
    public string? ConsumerNumber { get; set; }
    /// <summary>IESCO, SSGC, PTCL, DHA, school name, ...</summary>
    public string? Provider { get; set; }
    /// <summary>Expense account bills post to; falls back to a type default when null.</summary>
    public int? ExpenseAccountId { get; set; }
    public Account? ExpenseAccount { get; set; }
    public bool IsActive { get; set; } = true;
    public List<UtilityBill> Bills { get; set; } = [];
}

public class UtilityBill : AuditableEntity
{
    public int ConnectionId { get; set; }
    public UtilityConnection Connection { get; set; } = null!;
    /// <summary>Billing month (stored as the 1st of the month).</summary>
    public DateOnly BillMonth { get; set; }
    public DateOnly? DueDate { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Bill/invoice reference number.</summary>
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public DateOnly? PaidDate { get; set; }
    public int? VoucherId { get; set; }
    public bool IsPaid => VoucherId is not null;
}
