using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tender.Infrastructure;

/// <summary>Used by `dotnet ef` only; no live database connection needed.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TenderDbContext>
{
    public TenderDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenderDbContext>()
            .UseMySql("Server=localhost;Database=erp_tender;User=root;Password=;",
                new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;
        return new TenderDbContext(options, new DesignTimeCurrentUser());
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
