using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledger.Infrastructure;

/// <summary>Used by `dotnet ef` only; no live database connection needed.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseMySql("Server=localhost;Database=erp_ledger;User=root;Password=;",
                new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;
        return new LedgerDbContext(options, new DesignTimeCurrentUser());
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
