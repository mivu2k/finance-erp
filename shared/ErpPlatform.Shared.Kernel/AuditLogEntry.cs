namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// Per-module audit trail row. Each module owns its own table in its own database;
/// this is the shared shape so the audit viewer looks the same everywhere.
/// </summary>
public class AuditLogEntry
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? IpAddress { get; set; }
    public string? Browser { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public DateTime TimestampUtc { get; set; }
}
