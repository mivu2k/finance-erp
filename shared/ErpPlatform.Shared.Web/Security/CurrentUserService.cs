using System.Security.Claims;
using ErpPlatform.Shared.Identity;
using ErpPlatform.Shared.Kernel;
using Microsoft.AspNetCore.Http;

namespace ErpPlatform.Shared.Web.Security;

/// <summary>
/// Reads the signed-in user off the current request. Every module's audit pipeline
/// resolves this, so it lives in the shared layer rather than in any one app.
/// </summary>
public class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUserService
{
    private HttpContext? Ctx => accessor.HttpContext;

    public string? UserId => Ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? UserName => Ctx?.User.Identity?.Name;
    public string? IpAddress => Ctx?.Connection.RemoteIpAddress?.ToString();

    public string? Browser => Ctx?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua
        ? (ua.Length > 250 ? ua[..250] : ua)
        : null;

    public bool HasPermission(string permission) =>
        Ctx?.User.HasClaim(PermissionCatalog.ClaimType, permission) ?? false;

    public bool CanAccessModule(string moduleKey) =>
        Ctx?.User.CanAccessModule(moduleKey) ?? false;
}
