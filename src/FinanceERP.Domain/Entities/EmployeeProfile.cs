
namespace FinanceERP.Domain.Entities;

/// <summary>
/// The accounts module's own record of a person. Identity owns who someone *is*
/// (login, name, employee code); this owns what accounting needs to know about
/// them — which department they're costed to and, for directors, which ledger
/// account is theirs.
/// </summary>
/// <remarks>
/// <see cref="FullName"/>, <see cref="Email"/> and <see cref="EmployeeCode"/> are a
/// mirror of the identity database, refreshed by the employee sync. They're stored
/// here so payroll and reporting queries stay inside one database — the platform
/// has no cross-database joins.
/// </remarks>
public class EmployeeProfile : BaseEntity
{
    /// <summary>The Identity user id. This is the join key used across the platform.</summary>
    public string UserId { get; set; } = string.Empty;

    // --- mirrored from identity ---
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? EmployeeCode { get; set; }
    public bool IsActive { get; set; } = true;

    // --- owned by accounts ---
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    /// <summary>For directors: their dedicated ledger account in the chart of accounts.</summary>
    public int? LedgerAccountId { get; set; }
    public Account? LedgerAccount { get; set; }
}
