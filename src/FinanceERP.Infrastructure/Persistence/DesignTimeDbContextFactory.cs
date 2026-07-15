using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FinanceERP.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` only; no live database connection needed.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("Server=localhost;Database=finance_erp;User=root;Password=;",
                Microsoft.EntityFrameworkCore.ServerVersion.Parse("8.0.36-mysql"))
            .Options;
        return new AppDbContext(options, new DesignTimeCurrentUser());
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
