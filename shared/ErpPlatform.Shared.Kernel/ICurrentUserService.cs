namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// Supplies the current user/request context to the audit pipeline and to
/// permission checks made outside a Razor component.
/// </summary>
public interface ICurrentUserService
{
    string? UserId { get; }
    string? UserName { get; }
    string? IpAddress { get; }
    string? Browser { get; }
    bool HasPermission(string permission);
}
