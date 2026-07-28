using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Repair.Infrastructure;

/// <summary>Used by `dotnet ef` only; no live database connection needed.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RepairDbContext>
{
    public RepairDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RepairDbContext>()
            .UseMySql("Server=localhost;Database=erp_repair;User=root;Password=;",
                new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;
        return new RepairDbContext(options, new DesignTimeCurrentUser());
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
