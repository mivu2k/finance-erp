using ErpPlatform.Shared.Identity;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinanceERP.Infrastructure.Persistence;

public static class DbSeeder
{
    /// <summary>
    /// Brings the accounts database up to date. Roles, permissions and the admin
    /// account are no longer seeded here — they belong to the shared identity
    /// database and are handled by <c>IdentitySeeder</c>.
    /// </summary>
    public static async Task SeedAsync(AppDbContext db, IPlatformUserDirectory directory, ILogger logger)
    {
        await db.Database.MigrateAsync();

        await SeedChartOfAccountsAsync(db);
        await SeedDefaultsAsync(db);
        await SeedPayComponentsAsync(db);
        await SyncEmployeeProfilesAsync(db, directory, logger);
    }

    /// <summary>
    /// Mirrors platform users who can enter Finance into the accounts database.
    /// Payroll and reporting query this table instead of reaching across to the
    /// identity database, which keeps every finance query inside one connection.
    /// Accounts-owned fields (department, ledger account) are never overwritten.
    /// </summary>
    public static async Task SyncEmployeeProfilesAsync(
        AppDbContext db, IPlatformUserDirectory directory, ILogger logger)
    {
        var users = await directory.ListForModuleAsync(AppModules.Finance);
        var existing = await db.EmployeeProfiles.ToDictionaryAsync(p => p.UserId);
        var added = 0;

        foreach (var u in users)
        {
            if (existing.TryGetValue(u.UserId, out var profile))
            {
                profile.FullName = u.FullName;
                profile.Email = u.Email;
                profile.EmployeeCode = u.EmployeeCode;
                profile.IsActive = u.IsActive;
            }
            else
            {
                db.EmployeeProfiles.Add(new EmployeeProfile
                {
                    UserId = u.UserId,
                    FullName = u.FullName,
                    Email = u.Email,
                    EmployeeCode = u.EmployeeCode,
                    IsActive = u.IsActive
                });
                added++;
            }
        }

        // Someone who lost Finance access keeps their profile — payslips and
        // vouchers still point at it — but stops showing up as selectable.
        var current = users.Select(u => u.UserId).ToHashSet();
        foreach (var (userId, profile) in existing)
            if (!current.Contains(userId)) profile.IsActive = false;

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync();
            if (added > 0) logger.LogInformation("Mirrored {Count} new finance employee profiles", added);
        }
    }

    /// <summary>Starter salary component catalog. Amounts are zero — each employee's
    /// structure sets the real value; these just give payroll something to pick from.</summary>
    private static async Task SeedPayComponentsAsync(AppDbContext db)
    {
        if (await db.PayComponents.AnyAsync()) return;

        var accounts = await db.Accounts.ToDictionaryAsync(a => a.Code, a => a.Id);
        int? Acct(string code) => accounts.TryGetValue(code, out var id) ? id : null;

        // (code, name, kind, calc, defaultValue, accountCode, sortOrder)
        var rows = new (string Code, string Name, PayComponentKind Kind, PayComponentCalc Calc,
            decimal Value, string? Account, int Sort)[]
        {
            ("HRA",   "House Rent Allowance", PayComponentKind.Allowance, PayComponentCalc.PercentOfBasic, 40m, "5140", 10),
            ("TRANS", "Transport Allowance",  PayComponentKind.Allowance, PayComponentCalc.FixedAmount,     0m, "5140", 20),
            ("MED",   "Medical Allowance",    PayComponentKind.Allowance, PayComponentCalc.PercentOfBasic, 10m, "5140", 30),
            ("UTIL",  "Utility Allowance",    PayComponentKind.Allowance, PayComponentCalc.FixedAmount,     0m, "5140", 40),
            ("MOB",   "Mobile Allowance",     PayComponentKind.Allowance, PayComponentCalc.FixedAmount,     0m, "5140", 50),
            ("SPEC",  "Special Allowance",    PayComponentKind.Allowance, PayComponentCalc.FixedAmount,     0m, "5140", 60),
            ("TAX",   "Income Tax",           PayComponentKind.Deduction, PayComponentCalc.PercentOfBasic,  0m, "2300", 70),
            ("EOBI",  "EOBI / Social Security", PayComponentKind.Deduction, PayComponentCalc.FixedAmount,   0m, "2300", 80),
            ("OTHER", "Other Deduction",      PayComponentKind.Deduction, PayComponentCalc.FixedAmount,     0m, "2400", 90),
        };

        foreach (var r in rows)
            db.PayComponents.Add(new PayComponent
            {
                Code = r.Code, Name = r.Name, Kind = r.Kind, Calc = r.Calc,
                DefaultValue = r.Value, AccountId = Acct(r.Account!), SortOrder = r.Sort, IsSystem = true
            });
        await db.SaveChangesAsync();
    }

    private static async Task SeedChartOfAccountsAsync(AppDbContext db)
    {
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

            // Staff and director spend never share a head, so the trial balance and
            // income statement separate the two without any extra filtering.
            ("5200", "Employee Expenses", AccountType.Expense, "5000", false),
            ("5210", "Employee Travel", AccountType.Expense, "5200", true),
            ("5220", "Employee Entertainment", AccountType.Expense, "5200", true),
            ("5230", "Employee Fuel", AccountType.Expense, "5200", true),
            ("5240", "Employee Meals", AccountType.Expense, "5200", true),
            ("5250", "Employee Training", AccountType.Expense, "5200", true),
            ("5290", "Employee Miscellaneous", AccountType.Expense, "5200", true),

            ("5400", "Director Expenses", AccountType.Expense, "5000", false),
            ("5410", "Director Travel", AccountType.Expense, "5400", true),
            ("5420", "Director Entertainment", AccountType.Expense, "5400", true),
            ("5430", "Director Fuel", AccountType.Expense, "5400", true),
            ("5440", "Director Meals", AccountType.Expense, "5400", true),
            ("5490", "Director Miscellaneous", AccountType.Expense, "5400", true),

            ("5300", "Interest Expense", AccountType.Expense, "5000", true),
            ("5310", "Investment Loss", AccountType.Expense, "5000", true),
            ("5900", "Miscellaneous", AccountType.Expense, "5000", true),
        };

        // Only codes that don't exist yet are added, so an install that predates a new
        // head (the employee/director expense sub-trees) picks it up on next startup
        // without disturbing accounts the accountant has since edited.
        var byCode = await db.Accounts.ToDictionaryAsync(a => a.Code);
        foreach (var r in rows)
        {
            if (byCode.ContainsKey(r.Code)) continue;
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
