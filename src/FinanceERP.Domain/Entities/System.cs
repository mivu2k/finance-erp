using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? IpAddress { get; set; }
    public string? Browser { get; set; }
    public string Action { get; set; } = string.Empty; // Created / Modified / Deleted
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }
    public NotificationType Type { get; set; }
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Key/value application settings (company name, currency, low-cash threshold, ...).</summary>
public class AppSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public static class SettingKeys
{
    public const string CompanyName = "Company.Name";
    public const string Currency = "Company.Currency";
    public const string LowCashThreshold = "Alerts.LowCashThreshold";
}
