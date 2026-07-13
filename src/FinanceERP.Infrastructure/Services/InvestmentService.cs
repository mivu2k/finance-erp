using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class InvestmentService(AppDbContext db, IVoucherService voucherService) : IInvestmentService
{
    public async Task<PagedResult<Investment>> ListAsync(ReportFilter f)
    {
        var q = db.Investments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(i => i.Name.Contains(f.Search));
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(i => i.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        return new PagedResult<Investment>(items, total);
    }

    public Task<Investment?> GetAsync(int id) =>
        db.Investments.Include(i => i.Transactions.OrderBy(t => t.Date))
            .FirstOrDefaultAsync(i => i.Id == id);

    public async Task<Investment> CreateAsync(Investment investment, int cashAccountId)
    {
        if (investment.Amount <= 0) throw new InvalidOperationException("Amount must be positive.");
        var invAccount = await db.Accounts.FirstAsync(a => a.Code == "1500");

        db.Investments.Add(investment);
        await db.SaveChangesAsync();

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashPayment, investment.StartDate,
            $"Investment — {investment.Name}", "Investment", investment.Id,
            [
                (invAccount.Id, investment.Amount, 0m, investment.Name),
                (cashAccountId, 0m, investment.Amount, investment.Name)
            ]);

        investment.Transactions.Add(new InvestmentTransaction
        {
            Type = InvestmentTxnType.Deposit, Date = investment.StartDate,
            Amount = investment.Amount, VoucherId = voucher.Id
        });
        await db.SaveChangesAsync();
        return investment;
    }

    public async Task<Voucher> AddTransactionAsync(int investmentId, InvestmentTxnType type, decimal amount,
        DateOnly date, int cashAccountId, string? notes)
    {
        if (amount <= 0) throw new InvalidOperationException("Amount must be positive.");
        var inv = await db.Investments.Include(i => i.Transactions).FirstAsync(i => i.Id == investmentId);
        var invAccount = await db.Accounts.FirstAsync(a => a.Code == "1500");
        var incomeAccount = await db.Accounts.FirstAsync(a => a.Code == "4200");
        var lossAccount = await db.Accounts.FirstAsync(a => a.Code == "5310");

        var narration = $"Investment {inv.Name} — {type}";
        List<(int, decimal, decimal, string?)> lines = type switch
        {
            InvestmentTxnType.Deposit => [(invAccount.Id, amount, 0m, narration), (cashAccountId, 0m, amount, narration)],
            InvestmentTxnType.Profit => [(cashAccountId, amount, 0m, narration), (incomeAccount.Id, 0m, amount, narration)],
            InvestmentTxnType.Loss => [(lossAccount.Id, amount, 0m, narration), (invAccount.Id, 0m, amount, narration)],
            InvestmentTxnType.Withdrawal => [(cashAccountId, amount, 0m, narration), (invAccount.Id, 0m, amount, narration)],
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        var voucher = await voucherService.PostSystemVoucherAsync(
            type is InvestmentTxnType.Deposit ? VoucherType.CashPayment : VoucherType.CashReceipt,
            date, narration, "Investment", inv.Id, lines);

        inv.Transactions.Add(new InvestmentTransaction
        {
            Type = type, Date = date, Amount = amount, Notes = notes, VoucherId = voucher.Id
        });
        switch (type)
        {
            case InvestmentTxnType.Deposit: inv.Amount += amount; break;
            case InvestmentTxnType.Profit: inv.TotalProfit += amount; break;
            case InvestmentTxnType.Loss: inv.TotalProfit -= amount; inv.Amount -= amount; break;
            case InvestmentTxnType.Withdrawal:
                inv.TotalWithdrawn += amount;
                inv.Status = inv.TotalWithdrawn >= inv.Amount ? InvestmentStatus.Closed : InvestmentStatus.PartiallyWithdrawn;
                break;
        }
        await db.SaveChangesAsync();
        return voucher;
    }
}
