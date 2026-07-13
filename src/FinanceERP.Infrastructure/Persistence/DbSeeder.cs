using System.Security.Claims;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Domain.Security;
using FinanceERP.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FinanceERP.Infrastructure.Persistence;

public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager, IConfiguration config, ILogger logger)
    {
        await db.Database.MigrateAsync();

        await SeedRolesAsync(roleManager);
        await SeedAdminUserAsync(userManager, config, logger);
        await SeedChartOfAccountsAsync(db);
        await SeedDefaultsAsync(db);
    }

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roleManager)
    {
        foreach (var role in AppRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        var matrix = new Dictionary<string, string[]>
        {
            [AppRoles.SuperAdmin] = Permissions.All.ToArray(),
            [AppRoles.Admin] = Permissions.All.Where(p => p != Permissions.SettingsManage).ToArray(),
            [AppRoles.Director] =
            [
                Permissions.AccountsView, Permissions.LedgerView, Permissions.VouchersView,
                Permissions.ReportsView, Permissions.ReportsExport, Permissions.PettyCashView,
                Permissions.PettyCashAssign, Permissions.RequestsViewAll, Permissions.DirectorFundsRequest,
                Permissions.DirectorFundsView, Permissions.AdvancesViewAll, Permissions.AdvancesApprove,
                Permissions.LoansView, Permissions.InvestmentsView, Permissions.ThirdPartiesView
            ],
            [AppRoles.FinanceManager] =
            [
                Permissions.AccountsView, Permissions.AccountsManage, Permissions.LedgerView,
                Permissions.VouchersView, Permissions.VouchersCreate, Permissions.VouchersEdit,
                Permissions.VouchersPost, Permissions.ReportsView, Permissions.ReportsExport,
                Permissions.PettyCashView, Permissions.PettyCashManage, Permissions.RequestsViewAll,
                Permissions.RequestsApproveAdmin, Permissions.AdvancesViewAll, Permissions.AdvancesApprove,
                Permissions.AdvancesManage, Permissions.LoansView, Permissions.LoansManage,
                Permissions.InvestmentsView, Permissions.InvestmentsManage,
                Permissions.ThirdPartiesView, Permissions.ThirdPartiesManage
            ],
            [AppRoles.Accountant] =
            [
                Permissions.AccountsView, Permissions.LedgerView, Permissions.VouchersView,
                Permissions.VouchersCreate, Permissions.VouchersEdit, Permissions.VouchersPost,
                Permissions.ReportsView, Permissions.ReportsExport, Permissions.PettyCashView,
                Permissions.PettyCashManage, Permissions.RequestsViewAll, Permissions.RequestsPay,
                Permissions.AdvancesViewAll, Permissions.AdvancesManage,
                Permissions.LoansView, Permissions.InvestmentsView,
                Permissions.ThirdPartiesView, Permissions.ThirdPartiesManage
            ],
            [AppRoles.Manager] =
            [
                Permissions.RequestsCreate, Permissions.RequestsViewOwn, Permissions.RequestsApproveManager,
                Permissions.AdvancesCreate, Permissions.AdvancesViewOwn, Permissions.ReportsView
            ],
            [AppRoles.Employee] =
            [
                Permissions.RequestsCreate, Permissions.RequestsViewOwn,
                Permissions.AdvancesCreate, Permissions.AdvancesViewOwn
            ],
            [AppRoles.Auditor] =
            [
                Permissions.AccountsView, Permissions.LedgerView, Permissions.VouchersView,
                Permissions.ReportsView, Permissions.ReportsExport, Permissions.AuditView,
                Permissions.RequestsViewAll, Permissions.AdvancesViewAll, Permissions.LoansView,
                Permissions.InvestmentsView, Permissions.ThirdPartiesView, Permissions.PettyCashView
            ],
            [AppRoles.Viewer] =
            [
                Permissions.AccountsView, Permissions.LedgerView, Permissions.VouchersView, Permissions.ReportsView
            ]
        };

        foreach (var (roleName, perms) in matrix)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null) continue;
            var existing = (await roleManager.GetClaimsAsync(role))
                .Where(c => c.Type == Permissions.ClaimType).Select(c => c.Value).ToHashSet();
            foreach (var p in perms.Where(p => !existing.Contains(p)))
                await roleManager.AddClaimAsync(role, new Claim(Permissions.ClaimType, p));
        }
    }

    private static async Task SeedAdminUserAsync(UserManager<ApplicationUser> userManager, IConfiguration config, ILogger logger)
    {
        var email = config["Seed:AdminEmail"] ?? "admin@financeerp.local";
        var password = config["Seed:AdminPassword"] ?? "ChangeMe!123";

        if (await userManager.FindByEmailAsync(email) is not null) return;

        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = "System Administrator",
            IsActive = true
        };
        var result = await userManager.CreateAsync(admin, password);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(admin, AppRoles.SuperAdmin);
            logger.LogInformation("Seeded Super Admin {Email}", email);
        }
        else
        {
            logger.LogError("Failed to seed admin: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    private static async Task SeedChartOfAccountsAsync(AppDbContext db)
    {
        if (await db.Accounts.AnyAsync()) return;

        // (code, name, type, parentCode, isPostable)
        var rows = new (string Code, string Name, AccountType Type, string? Parent, bool Postable)[]
        {
            ("1000", "Assets", AccountType.Asset, null, false),
            ("1100", "Cash in Hand", AccountType.Asset, "1000", true),
            ("1200", "Bank Accounts", AccountType.Asset, "1000", false),
            ("1201", "Main Bank Account", AccountType.Asset, "1200", true),
            ("1300", "Petty Cash", AccountType.Asset, "1000", true),
            ("1400", "Loans Given", AccountType.Asset, "1000", true),
            ("1500", "Investments", AccountType.Asset, "1000", true),
            ("1600", "Receivables", AccountType.Asset, "1000", false),
            ("1700", "Employee Advances", AccountType.Asset, "1000", true),

            ("2000", "Liabilities", AccountType.Liability, null, false),
            ("2100", "Payables", AccountType.Liability, "2000", false),
            ("2200", "Third Party Loans", AccountType.Liability, "2000", true),
            ("2300", "Taxes Payable", AccountType.Liability, "2000", true),
            ("2400", "Salaries Payable", AccountType.Liability, "2000", true),

            ("3000", "Equity", AccountType.Equity, null, false),
            ("3100", "Owner Capital", AccountType.Equity, "3000", true),
            ("3200", "Director Capital", AccountType.Equity, "3000", false),
            ("3900", "Retained Earnings", AccountType.Equity, "3000", true),

            ("4000", "Income", AccountType.Income, null, false),
            ("4100", "Sales", AccountType.Income, "4000", true),
            ("4200", "Investment Income", AccountType.Income, "4000", true),
            ("4300", "Interest Income", AccountType.Income, "4000", true),
            ("4900", "Other Income", AccountType.Income, "4000", true),

            ("5000", "Expenses", AccountType.Expense, null, false),
            ("5100", "Office Expenses", AccountType.Expense, "5000", true),
            ("5110", "Fuel", AccountType.Expense, "5000", true),
            ("5120", "Utilities", AccountType.Expense, "5000", true),
            ("5130", "Internet", AccountType.Expense, "5000", true),
            ("5140", "Salary Expense", AccountType.Expense, "5000", true),
            ("5150", "Repair", AccountType.Expense, "5000", true),
            ("5160", "Maintenance", AccountType.Expense, "5000", true),
            ("5170", "Entertainment", AccountType.Expense, "5000", true),
            ("5180", "Travel", AccountType.Expense, "5000", true),
            ("5190", "Marketing", AccountType.Expense, "5000", true),
            ("5300", "Interest Expense", AccountType.Expense, "5000", true),
            ("5310", "Investment Loss", AccountType.Expense, "5000", true),
            ("5900", "Miscellaneous", AccountType.Expense, "5000", true),
        };

        var byCode = new Dictionary<string, Account>();
        foreach (var r in rows)
        {
            var acc = new Account
            {
                Code = r.Code, Name = r.Name, Type = r.Type,
                IsSystem = true, IsPostable = r.Postable,
                Parent = r.Parent is null ? null : byCode[r.Parent]
            };
            byCode[r.Code] = acc;
            db.Accounts.Add(acc);
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedDefaultsAsync(AppDbContext db)
    {
        if (!await db.Departments.AnyAsync())
        {
            db.Departments.AddRange(
                new Department { Name = "Administration" },
                new Department { Name = "Finance" },
                new Department { Name = "Operations" },
                new Department { Name = "Sales" },
                new Department { Name = "IT" });
        }
        if (!await db.CostCenters.AnyAsync())
        {
            db.CostCenters.AddRange(
                new CostCenter { Name = "Head Office" },
                new CostCenter { Name = "Field" });
        }
        if (!await db.AppSettings.AnyAsync())
        {
            db.AppSettings.AddRange(
                new AppSetting { Key = SettingKeys.CompanyName, Value = "My Company (Pvt) Ltd" },
                new AppSetting { Key = SettingKeys.Currency, Value = "PKR" },
                new AppSetting { Key = SettingKeys.LowCashThreshold, Value = "50000" });
        }
        await db.SaveChangesAsync();
    }
}
