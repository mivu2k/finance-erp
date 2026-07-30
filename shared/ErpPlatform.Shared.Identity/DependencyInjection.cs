using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ErpPlatform.Shared.Identity;

public static class DependencyInjection
{
    /// <summary>
    /// Wires the shared identity database and ASP.NET Identity. Call this once in
    /// the host, before any module registration — the module seeders depend on
    /// <see cref="RoleManager{TRole}"/> being available.
    /// </summary>
    public static IServiceCollection AddPlatformIdentity(
        this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("IdentityConnection")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:IdentityConnection is not configured.");

        services.AddDbContext<PlatformIdentityDbContext>(o =>
            o.UseMySql(cs, ServerVersion.AutoDetect(cs)));

        services.AddScoped<IModuleAccessService, ModuleAccessService>();
        services.AddScoped<IPlatformUserDirectory, PlatformUserDirectory>();
        services.AddScoped<ICompanyProfileService, CompanyProfileService>();
        services.AddScoped<ILabelTemplateService, LabelTemplateService>();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.Password.RequiredLength = 8;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<PlatformIdentityDbContext>()
            .AddClaimsPrincipalFactory<PlatformClaimsPrincipalFactory>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        PermissionCatalog.Register(PlatformPermissions.All);

        return services;
    }
}
