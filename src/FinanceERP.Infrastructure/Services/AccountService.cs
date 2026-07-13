using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class AccountService(AppDbContext db) : IAccountService
{
    public async Task<List<Account>> GetTreeAsync() =>
        await db.Accounts.AsNoTracking().OrderBy(a => a.Code).ToListAsync();

    public async Task<List<Account>> GetPostableAsync() =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.IsPostable && a.IsActive)
            .OrderBy(a => a.Code).ToListAsync();

    public async Task<Account> SaveAsync(Account account)
    {
        if (account.Id == 0)
        {
            db.Accounts.Add(account);
        }
        else
        {
            var existing = await db.Accounts.FirstAsync(a => a.Id == account.Id);
            existing.Name = account.Name;
            existing.Code = account.Code;
            existing.Type = account.Type;
            existing.ParentId = account.ParentId;
            existing.IsActive = account.IsActive;
            existing.IsPostable = account.IsPostable;
            existing.Description = account.Description;
            account = existing;
        }
        await db.SaveChangesAsync();
        return account;
    }

    public async Task DeleteAsync(int id)
    {
        var account = await db.Accounts.Include(a => a.Children).FirstAsync(a => a.Id == id);
        if (account.IsSystem) throw new InvalidOperationException("System accounts cannot be deleted.");
        if (account.Children.Count > 0) throw new InvalidOperationException("Delete or move child accounts first.");
        if (await db.VoucherLines.AnyAsync(l => l.AccountId == id))
            throw new InvalidOperationException("Account has ledger entries; deactivate it instead.");
        db.Accounts.Remove(account); // soft delete via interceptor
        await db.SaveChangesAsync();
    }

    public async Task<decimal> GetBalanceAsync(int accountId, DateOnly? asOf = null)
    {
        var q = db.VoucherLines.AsNoTracking()
            .Where(l => l.AccountId == accountId && l.Voucher.Status == VoucherStatus.Posted);
        if (asOf is not null) q = q.Where(l => l.Voucher.Date <= asOf);
        var sums = await q.GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
            .FirstOrDefaultAsync();
        return (sums?.D ?? 0) - (sums?.C ?? 0);
    }

    public async Task<Account> EnsureChildAccountAsync(string parentCode, string name, bool isSystem = false)
    {
        var parent = await db.Accounts.FirstAsync(a => a.Code == parentCode);
        var existing = await db.Accounts.FirstOrDefaultAsync(a => a.ParentId == parent.Id && a.Name == name);
        if (existing is not null) return existing;

        var siblingCodes = await db.Accounts.Where(a => a.ParentId == parent.Id).Select(a => a.Code).ToListAsync();
        var next = 1;
        string code;
        do { code = $"{parent.Code}-{next++:D3}"; } while (siblingCodes.Contains(code));

        var account = new Account
        {
            Code = code, Name = name, Type = parent.Type,
            ParentId = parent.Id, IsPostable = true, IsSystem = isSystem
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }
}
