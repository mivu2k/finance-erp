using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class ReportService(AppDbContext db) : IReportService
{
    private IQueryable<Domain.Entities.VoucherLine> PostedLines(ReportFilter f)
    {
        var q = db.VoucherLines.AsNoTracking()
            .Where(l => l.Voucher.Status == VoucherStatus.Posted);
        if (f.From is not null) q = q.Where(l => l.Voucher.Date >= f.From);
        if (f.To is not null) q = q.Where(l => l.Voucher.Date <= f.To);
        if (f.AccountId is not null) q = q.Where(l => l.AccountId == f.AccountId);
        if (f.DepartmentId is not null) q = q.Where(l => l.DepartmentId == f.DepartmentId);
        if (f.CostCenterId is not null) q = q.Where(l => l.CostCenterId == f.CostCenterId);
        if (f.ProjectId is not null) q = q.Where(l => l.ProjectId == f.ProjectId);
        if (f.ThirdPartyId is not null) q = q.Where(l => l.ThirdPartyId == f.ThirdPartyId);
        if (f.VoucherType is not null) q = q.Where(l => l.Voucher.Type == f.VoucherType);
        return q;
    }

    public async Task<List<LedgerRowDto>> GeneralLedgerAsync(ReportFilter f)
    {
        var rows = await PostedLines(f)
            .OrderBy(l => l.Voucher.Date).ThenBy(l => l.VoucherId).ThenBy(l => l.LineNo)
            .Take(5000)
            .Select(l => new
            {
                l.Voucher.Date, l.Voucher.VoucherNo, l.VoucherId,
                l.Account.Code, AccountName = l.Account.Name,
                l.Description, l.Debit, l.Credit,
                CostCenter = l.CostCenter!.Name, Department = l.Department!.Name, Project = l.Project!.Name
            })
            .ToListAsync();

        decimal running = 0;
        return rows.Select(r =>
        {
            running += r.Debit - r.Credit;
            return new LedgerRowDto(r.Date, r.VoucherNo, r.VoucherId, r.Code, r.AccountName,
                r.Description, r.Debit, r.Credit, running, r.CostCenter, r.Department, r.Project);
        }).ToList();
    }

    public async Task<List<TrialBalanceRowDto>> TrialBalanceAsync(DateOnly? asOf)
    {
        var q = db.VoucherLines.AsNoTracking().Where(l => l.Voucher.Status == VoucherStatus.Posted);
        if (asOf is not null) q = q.Where(l => l.Voucher.Date <= asOf);
        var sums = await q.GroupBy(l => new { l.Account.Code, l.Account.Name, l.Account.Type })
            .Select(g => new { g.Key.Code, g.Key.Name, g.Key.Type, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
            .OrderBy(x => x.Code)
            .ToListAsync();
        return sums.Select(s =>
        {
            var net = s.D - s.C;
            return new TrialBalanceRowDto(s.Code, s.Name, s.Type.ToString(),
                net > 0 ? net : 0, net < 0 ? -net : 0);
        }).Where(r => r.Debit != 0 || r.Credit != 0).ToList();
    }

    public async Task<List<TrialBalanceRowDto>> IncomeStatementAsync(DateOnly from, DateOnly to)
    {
        var sums = await db.VoucherLines.AsNoTracking()
            .Where(l => l.Voucher.Status == VoucherStatus.Posted && l.Voucher.Date >= from && l.Voucher.Date <= to
                && (l.Account.Type == AccountType.Income || l.Account.Type == AccountType.Expense))
            .GroupBy(l => new { l.Account.Code, l.Account.Name, l.Account.Type })
            .Select(g => new { g.Key.Code, g.Key.Name, g.Key.Type, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
            .OrderBy(x => x.Type).ThenBy(x => x.Code)
            .ToListAsync();
        return sums.Select(s => new TrialBalanceRowDto(s.Code, s.Name, s.Type.ToString(),
                s.Type == AccountType.Expense ? s.D - s.C : 0,
                s.Type == AccountType.Income ? s.C - s.D : 0))
            .Where(r => r.Debit != 0 || r.Credit != 0).ToList();
    }

    public async Task<List<TrialBalanceRowDto>> BalanceSheetAsync(DateOnly asOf)
    {
        var sums = await db.VoucherLines.AsNoTracking()
            .Where(l => l.Voucher.Status == VoucherStatus.Posted && l.Voucher.Date <= asOf
                && (l.Account.Type == AccountType.Asset || l.Account.Type == AccountType.Liability || l.Account.Type == AccountType.Equity))
            .GroupBy(l => new { l.Account.Code, l.Account.Name, l.Account.Type })
            .Select(g => new { g.Key.Code, g.Key.Name, g.Key.Type, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
            .OrderBy(x => x.Type).ThenBy(x => x.Code)
            .ToListAsync();

        var rows = sums.Select(s => new TrialBalanceRowDto(s.Code, s.Name, s.Type.ToString(),
                s.Type == AccountType.Asset ? s.D - s.C : 0,
                s.Type != AccountType.Asset ? s.C - s.D : 0))
            .Where(r => r.Debit != 0 || r.Credit != 0).ToList();

        // Current-period P&L rolls into equity so the sheet balances.
        var pl = await db.VoucherLines.AsNoTracking()
            .Where(l => l.Voucher.Status == VoucherStatus.Posted && l.Voucher.Date <= asOf
                && (l.Account.Type == AccountType.Income || l.Account.Type == AccountType.Expense))
            .GroupBy(_ => 1)
            .Select(g => g.Sum(x => x.Credit) - g.Sum(x => x.Debit))
            .FirstOrDefaultAsync();
        if (pl != 0)
            rows.Add(new TrialBalanceRowDto("3900*", "Current Earnings", "Equity", pl < 0 ? -pl : 0, pl > 0 ? pl : 0));
        return rows;
    }

    public async Task<List<LedgerRowDto>> CashBookAsync(ReportFilter f)
    {
        var cashIds = await db.Accounts.AsNoTracking()
            .Where(a => a.Code == "1100" || a.Code == "1300" || (a.Parent != null && a.Parent.Code == "1200"))
            .Select(a => a.Id).ToListAsync();
        f.AccountId = null;
        var rows = await PostedLines(f).Where(l => cashIds.Contains(l.AccountId))
            .OrderBy(l => l.Voucher.Date).ThenBy(l => l.VoucherId)
            .Take(5000)
            .Select(l => new
            {
                l.Voucher.Date, l.Voucher.VoucherNo, l.VoucherId, l.Account.Code,
                AccountName = l.Account.Name, l.Description, l.Debit, l.Credit
            }).ToListAsync();
        decimal running = 0;
        return rows.Select(r =>
        {
            running += r.Debit - r.Credit;
            return new LedgerRowDto(r.Date, r.VoucherNo, r.VoucherId, r.Code, r.AccountName,
                r.Description, r.Debit, r.Credit, running, null, null, null);
        }).ToList();
    }

    public async Task<List<CashFlowPointDto>> CashFlowAsync(DateOnly from, DateOnly to)
    {
        var cashIds = await db.Accounts.AsNoTracking()
            .Where(a => a.Code == "1100" || a.Code == "1300" || (a.Parent != null && a.Parent.Code == "1200"))
            .Select(a => a.Id).ToListAsync();
        var points = await db.VoucherLines.AsNoTracking()
            .Where(l => l.Voucher.Status == VoucherStatus.Posted && cashIds.Contains(l.AccountId)
                && l.Voucher.Date >= from && l.Voucher.Date <= to)
            .GroupBy(l => l.Voucher.Date)
            .Select(g => new { Date = g.Key, Inflow = g.Sum(x => x.Debit), Outflow = g.Sum(x => x.Credit) })
            .OrderBy(p => p.Date)
            .ToListAsync();
        return points.Select(p => new CashFlowPointDto(p.Date, p.Inflow, p.Outflow)).ToList();
    }

    public async Task<List<ExpenseBreakdownDto>> ExpenseBreakdownAsync(DateOnly from, DateOnly to)
    {
        var rows = await db.VoucherLines.AsNoTracking()
            .Where(l => l.Voucher.Status == VoucherStatus.Posted && l.Account.Type == AccountType.Expense
                && l.Voucher.Date >= from && l.Voucher.Date <= to)
            .GroupBy(l => l.Account.Name)
            .Select(g => new { Category = g.Key, Amount = g.Sum(x => x.Debit) - g.Sum(x => x.Credit) })
            .OrderByDescending(e => e.Amount)
            .ToListAsync();
        return rows.Select(r => new ExpenseBreakdownDto(r.Category, r.Amount)).ToList();
    }

    public async Task<DailySummaryDto> DailySummaryAsync(string? forUserId = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var posted = db.VoucherLines.AsNoTracking().Where(l => l.Voucher.Status == VoucherStatus.Posted);

        var todaySums = await posted.Where(l => l.Voucher.Date == today)
            .GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
            .FirstOrDefaultAsync();

        // List<string> (not array): array Contains binds to a span overload on .NET 10
        // that EF Core 9 cannot evaluate.
        async Task<decimal> BalanceByCodes(params IEnumerable<string> codesIn)
        {
            var codes = codesIn.ToList();
            var s = await posted.Where(l => codes.Contains(l.Account.Code) ||
                    (l.Account.Parent != null && codes.Contains(l.Account.Parent.Code)))
                .GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
                .FirstOrDefaultAsync();
            return (s?.D ?? 0) - (s?.C ?? 0);
        }

        var pendingRequests = await db.PaymentRequests.CountAsync(r =>
            r.Status == RequestStatus.PendingAccountant);
        var pendingApprovals = await db.PaymentRequests.CountAsync(r =>
            r.Status == RequestStatus.PendingManager || r.Status == RequestStatus.PendingAdmin);
        var outstandingAdvances = await db.EmployeeAdvances
            .Where(a => a.Status == AdvanceStatus.Disbursed || a.Status == AdvanceStatus.Repaying)
            .SumAsync(a => a.Amount - a.RepaidAmount);
        var loansGiven = await BalanceByCodes("1400");
        var loansTaken = -await BalanceByCodes("2200");
        var investments = await BalanceByCodes("1500");

        return new DailySummaryDto(
            todaySums?.D ?? 0, todaySums?.C ?? 0,
            await BalanceByCodes("1100"),
            await BalanceByCodes("1300"),
            await BalanceByCodes("1200"),
            pendingRequests, pendingApprovals, outstandingAdvances,
            loansGiven, loansTaken, investments);
    }
}
