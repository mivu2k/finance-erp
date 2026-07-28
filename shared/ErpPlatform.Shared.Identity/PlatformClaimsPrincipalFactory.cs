using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ErpPlatform.Shared.Identity;

/// <summary>
/// Stamps the modules a user may enter onto their principal at sign-in, so the
/// portal and every nav guard can answer "can they see this app?" without hitting
/// the identity database on each render.
/// </summary>
/// <remarks>
/// Permission claims already arrive here for free: the base factory copies
/// AspNetRoleClaims of the user's roles onto the principal. Module access is the
/// only thing that needs computing, and it changes rarely — a user whose access is
/// edited picks it up on their next sign-in or on cookie revalidation.
/// </remarks>
public class PlatformClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentityOptions> options,
    IModuleAccessService moduleAccess)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, options)
{
    public override async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
        var principal = await base.CreateAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;

        if (!string.IsNullOrEmpty(user.FullName))
            identity.AddClaim(new Claim("full_name", user.FullName));

        foreach (var key in await moduleAccess.GetAccessibleModuleKeysAsync(user.Id))
            identity.AddClaim(new Claim(PermissionCatalog.ModuleClaimType, key));

        return principal;
    }
}
