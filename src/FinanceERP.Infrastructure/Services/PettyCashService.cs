using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class PettyCashService(AppDbContext db, IVoucherService voucherService) : IPettyCashService
{
    public Task<List<PettyCashAssignment>> ListAssignmentsAsync() =>
        db.PettyCashAssignments.AsNoTracking().Include(p => p.PettyCashAccount)
            .OrderByDescending(p => p.Id).Take(200).ToListAsync();

    /// <summary>Director assigns a float: Dr Petty Cash, Cr source (Cash/Bank). Auto-posted.</summary>
    public async Task<PettyCashAssignment> AssignAsync(PettyCashAssignment assignment, int sourceAccountId)
    {
        if (assignment.Amount <= 0) throw new InvalidOperationException("Amount must be positive.");
        db.PettyCashAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashPayment, assignment.Date,
            $"Petty cash assigned to {assignment.AccountantName}", "PettyCash", assignment.Id,
            [
                (assignment.PettyCashAccountId, assignment.Amount, 0m, $"Float to {assignment.AccountantName}"),
                (sourceAccountId, 0m, assignment.Amount, $"Petty cash float")
            ]);
        assignment.VoucherId = voucher.Id;
        await db.SaveChangesAsync();
        return assignment;
    }

    public async Task<(decimal Opening, decimal Received, decimal Paid, decimal Closing)> GetDayBookAsync(int pettyCashAccountId, DateOnly date)
    {
        var posted = db.VoucherLines.AsNoTracking()
            .Where(l => l.AccountId == pettyCashAccountId && l.Voucher.Status == VoucherStatus.Posted);

        var before = await posted.Where(l => l.Voucher.Date < date)
            .GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
            .FirstOrDefaultAsync();
        var today = await posted.Where(l => l.Voucher.Date == date)
            .GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
            .FirstOrDefaultAsync();

        var opening = (before?.D ?? 0) - (before?.C ?? 0);
        var received = today?.D ?? 0;
        var paid = today?.C ?? 0;
        return (opening, received, paid, opening + received - paid);
    }
}
