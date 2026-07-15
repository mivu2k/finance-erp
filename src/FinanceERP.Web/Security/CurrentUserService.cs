using System.Security.Claims;
using FinanceERP.Infrastructure.Persistence;

namespace FinanceERP.Web.Security;

public class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUserService
{
    private HttpContext? Ctx => accessor.HttpContext;

    public string? UserId => Ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? UserName => Ctx?.User.Identity?.Name;
    public string? IpAddress => Ctx?.Connection.RemoteIpAddress?.ToString();
    public string? Browser => Ctx?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua
        ? (ua.Length > 250 ? ua[..250] : ua) : null;

    public bool HasPermission(string permission) =>
        Ctx?.User.HasClaim(FinanceERP.Domain.Security.Permissions.ClaimType, permission) ?? false;
}
