using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GatePass.Infrastructure;

/// <summary>Used by `dotnet ef` only; no live database connection needed.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GatePassDbContext>
{
    public GatePassDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GatePassDbContext>()
            .UseMySql("Server=localhost;Database=erp_gatepass;User=root;Password=;",
                new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;
        return new GatePassDbContext(options, new DesignTimeCurrentUser());
    }

    private sealed class DesignTimeCurrentUser : ICurrentUserService
    {
        public string? UserId => null;
        public string? UserName => "design-time";
        public string? IpAddress => null;
        public string? Browser => null;
        public bool HasPermission(string permission) => false;
    }
}
