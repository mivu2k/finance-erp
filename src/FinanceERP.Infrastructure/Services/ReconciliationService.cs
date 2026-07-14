using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class ReconciliationService(AppDbContext db, ICurrentUserService currentUser) : IReconciliationService
{
    public Task<List<VoucherLine>> GetLinesAsync(int accountId, DateOnly from, DateOnly to, bool? reconciled = null)
    {
        var q = db.VoucherLines.AsNoTracking()
            .Include(l => l.Voucher)
            .Where(l => l.AccountId == accountId && l.Voucher.Status == VoucherStatus.Posted
                && l.Voucher.Date >= from && l.Voucher.Date <= to);
        if (reconciled is not null) q = q.Where(l => l.IsReconciled == reconciled);
        return q.OrderBy(l => l.Voucher.Date).ThenBy(l => l.VoucherId).ToListAsync();
    }

    public async Task SetReconciledAsync(IEnumerable<int> lineIds, bool reconciled)
    {
        var ids = lineIds.ToList();
        var lines = await db.VoucherLines.Where(l => ids.Contains(l.Id)).ToListAsync();
        foreach (var line in lines)
        {
            line.IsReconciled = reconciled;
            line.ReconciledAtUtc = reconciled ? DateTime.UtcNow : null;
            line.ReconciledBy = reconciled ? currentUser.UserName : null;
        }
        await db.SaveChangesAsync();
    }

    public async Task<(decimal Book, decimal Reconciled)> BalancesAsync(int accountId, DateOnly asOf)
    {
        var q = db.VoucherLines.AsNoTracking()
            .Where(l => l.AccountId == accountId && l.Voucher.Status == VoucherStatus.Posted && l.Voucher.Date <= asOf);
        var book = await q.GroupBy(_ => 1)
            .Select(g => g.Sum(x => x.Debit) - g.Sum(x => x.Credit)).FirstOrDefaultAsync();
        var rec = await q.Where(l => l.IsReconciled).GroupBy(_ => 1)
            .Select(g => g.Sum(x => x.Debit) - g.Sum(x => x.Credit)).FirstOrDefaultAsync();
        return (book, rec);
    }
}
