using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

/// <summary>Chart of Accounts node. Unlimited depth via ParentId.</summary>
public class Account : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public int? ParentId { get; set; }
    public Account? Parent { get; set; }
    public List<Account> Children { get; set; } = [];
    /// <summary>System accounts (Cash in Hand, Petty Cash, ...) cannot be deleted.</summary>
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Only leaf (postable) accounts can carry ledger entries.</summary>
    public bool IsPostable { get; set; } = true;
    public string? Description { get; set; }
}

public class Department : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class CostCenter : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class Project : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
