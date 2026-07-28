using ErpPlatform.Shared.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace ErpPlatform.Shared.Web.Security;

public class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim(PermissionCatalog.ClaimType, requirement.Permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Requires the user to have access to a module, independent of any single permission.</summary>
public class ModuleAccessRequirement(string moduleKey) : IAuthorizationRequirement
{
    public string ModuleKey { get; } = moduleKey;
}

public class ModuleAccessHandler : AuthorizationHandler<ModuleAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ModuleAccessRequirement requirement)
    {
        if (context.User.CanAccessModule(requirement.ModuleKey))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds an authorization policy on the fly for any registered permission name,
/// so no policy is ever hardcoded. Two policy shapes are understood:
/// a permission name from <see cref="PermissionCatalog"/>, and
/// <c>module:{key}</c> for "may this user enter this app at all".
/// </summary>
public class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    public const string ModulePolicyPrefix = "module:";

    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(ModulePolicyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = policyName[ModulePolicyPrefix.Length..];
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ModuleAccessRequirement(key))
                .Build());
        }

        // The catalog is filled by module registration at startup, so this covers
        // every module's permissions, not just Finance's.
        if (PermissionCatalog.All.Any(p => p.Name == policyName))
        {
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName))
                .Build());
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
