using Microsoft.AspNetCore.Identity;

namespace FinanceERP.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public string? EmployeeCode { get; set; }
    public int? DepartmentId { get; set; }
    public string? ManagerId { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>For directors: their dedicated ledger account in the COA.</summary>
    public int? LedgerAccountId { get; set; }
}
