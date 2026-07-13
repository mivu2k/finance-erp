using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class LoanService(AppDbContext db, IVoucherService voucherService, IAccountService accountService) : ILoanService
{
    public async Task<PagedResult<Loan>> ListAsync(ReportFilter f, LoanDirection? direction = null)
    {
        var q = db.Loans.AsNoTracking().Include(l => l.ThirdParty).AsQueryable();
        if (direction is not null) q = q.Where(l => l.Direction == direction);
        if (!string.IsNullOrWhiteSpace(f.Search))
            q = q.Where(l => l.LoanNo.Contains(f.Search) || l.ThirdParty.Name.Contains(f.Search));
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(l => l.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        return new PagedResult<Loan>(items, total);
    }

    public Task<Loan?> GetAsync(int id) =>
        db.Loans.Include(l => l.ThirdParty).Include(l => l.Installments.OrderBy(i => i.Number))
            .FirstOrDefaultAsync(l => l.Id == id);

    /// <summary>
    /// Loan Given: Dr Loans Given, Cr Cash. Loan Taken: Dr Cash, Cr Third Party Loans.
    /// </summary>
    public async Task<Loan> CreateAsync(Loan loan, int cashAccountId)
    {
        if (loan.Principal <= 0) throw new InvalidOperationException("Principal must be positive.");
        loan.LoanNo = $"LN-{DateTime.Today.Year}-{await db.Loans.IgnoreQueryFilters().CountAsync() + 1:D5}";

        var loansGiven = await db.Accounts.FirstAsync(a => a.Code == "1400");
        var loansTaken = await db.Accounts.FirstAsync(a => a.Code == "2200");

        var lines = loan.Direction == LoanDirection.Given
            ? new[] { (loansGiven.Id, loan.Principal, 0m, (string?)$"Loan given — {loan.LoanNo}"), (cashAccountId, 0m, loan.Principal, $"Loan given — {loan.LoanNo}") }
            : new[] { (cashAccountId, loan.Principal, 0m, (string?)$"Loan taken — {loan.LoanNo}"), (loansTaken.Id, 0m, loan.Principal, $"Loan taken — {loan.LoanNo}") };

        db.Loans.Add(loan);
        await db.SaveChangesAsync();

        var voucher = await voucherService.PostSystemVoucherAsync(
            loan.Direction == LoanDirection.Given ? VoucherType.CashPayment : VoucherType.CashReceipt,
            loan.StartDate, $"Loan {loan.LoanNo} ({loan.Direction})", "Loan", loan.Id, lines);
        loan.DisbursementVoucherId = voucher.Id;

        // Simple equal-installment schedule with flat interest, if a due date and count exist.
        if (loan.Installments.Count == 0 && loan.DueDate is not null)
        {
            var months = Math.Max(1, ((loan.DueDate.Value.Year - loan.StartDate.Year) * 12) + loan.DueDate.Value.Month - loan.StartDate.Month);
            var totalInterest = Math.Round(loan.Principal * loan.InterestRatePercent / 100m, 2);
            var perInstallment = Math.Round((loan.Principal + totalInterest) / months, 2);
            var perInterest = Math.Round(totalInterest / months, 2);
            for (var i = 1; i <= months; i++)
                loan.Installments.Add(new LoanInstallment
                {
                    Number = i, DueDate = loan.StartDate.AddMonths(i),
                    Amount = perInstallment, InterestPortion = perInterest
                });
        }
        await db.SaveChangesAsync();
        return loan;
    }

    public async Task<Voucher> PayInstallmentAsync(int installmentId, decimal amount, int cashAccountId, DateOnly date)
    {
        var inst = await db.LoanInstallments.Include(i => i.Loan).ThenInclude(l => l.ThirdParty).FirstAsync(i => i.Id == installmentId);
        var loan = inst.Loan;
        if (amount <= 0 || amount > inst.Amount - inst.PaidAmount) throw new InvalidOperationException("Invalid amount.");

        var loansGiven = await db.Accounts.FirstAsync(a => a.Code == "1400");
        var loansTaken = await db.Accounts.FirstAsync(a => a.Code == "2200");
        var interestIncome = await db.Accounts.FirstAsync(a => a.Code == "4300");
        var interestExpense = await db.Accounts.FirstAsync(a => a.Code == "5300");

        // Split interest proportionally to the payment.
        var interest = inst.Amount == 0 ? 0 : Math.Round(amount * inst.InterestPortion / inst.Amount, 2);
        var principal = amount - interest;

        var narration = $"Loan {loan.LoanNo} installment #{inst.Number} — {loan.ThirdParty.Name}";
        List<(int, decimal, decimal, string?)> lines;
        VoucherType vType;
        if (loan.Direction == LoanDirection.Given)
        {
            vType = VoucherType.CashReceipt;
            lines = [(cashAccountId, amount, 0m, narration), (loansGiven.Id, 0m, principal, narration)];
            if (interest > 0) lines.Add((interestIncome.Id, 0m, interest, $"Interest — {loan.LoanNo}"));
        }
        else
        {
            vType = VoucherType.CashPayment;
            lines = [(loansTaken.Id, principal, 0m, narration)];
            if (interest > 0) lines.Add((interestExpense.Id, interest, 0m, $"Interest — {loan.LoanNo}"));
            lines.Add((cashAccountId, 0m, amount, narration));
        }

        var voucher = await voucherService.PostSystemVoucherAsync(vType, date, narration, "Loan", loan.Id, lines);

        inst.PaidAmount += amount;
        inst.PaidDate = date;
        inst.PaymentVoucherId = voucher.Id;
        inst.Status = inst.PaidAmount >= inst.Amount ? InstallmentStatus.Paid : InstallmentStatus.PartiallyPaid;
        loan.RepaidAmount += principal;
        if (loan.Installments.Count > 0 && loan.Installments.All(i => i.Status == InstallmentStatus.Paid) ||
            loan.RepaidAmount >= loan.Principal)
            loan.Status = LoanStatus.Settled;
        await db.SaveChangesAsync();
        return voucher;
    }
}
