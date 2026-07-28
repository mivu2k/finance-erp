using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ErpPlatform.Shared.Identity;

/// <summary>Lets `dotnet ef` build the context without booting the host.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformIdentityDbContext>
{
    public PlatformIdentityDbContext CreateDbContext(string[] args)
    {
        const string cs = "Server=localhost;Port=3306;Database=erp_identity;User=finance;Password=DevPassword1!;";
        var options = new DbContextOptionsBuilder<PlatformIdentityDbContext>()
            .UseMySql(cs, new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;
        return new PlatformIdentityDbContext(options);
    }
}
