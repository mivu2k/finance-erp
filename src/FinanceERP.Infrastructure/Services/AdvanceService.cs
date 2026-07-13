using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Domain.Security;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class AdvanceService(
    AppDbContext db,
    ICurrentUserService currentUser,
    IVoucherService voucherService,
    IAccountService accountService,
    INotificationService notifications) : IAdvanceService
{
    private const string AdvancesParentCode = "1700"; // Employee Advances (Asset)

    public async Task<PagedResult<EmployeeAdvance>> ListAsync(ReportFilter f, string? employeeId = null)
    {
        var q = db.EmployeeAdvances.AsNoTracking().AsQueryable();
        if (employeeId is not null) q = q.Where(a => a.EmployeeId == employeeId);
        if (!string.IsNullOrWhiteSpace(f.Search))
            q = q.Where(a => a.AdvanceNo.Contains(f.Search) || a.EmployeeName.Contains(f.Search));
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(a => a.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        return new PagedResult<EmployeeAdvance>(items, total);
    }

    public Task<EmployeeAdvance?> GetAsync(int id) =>
        db.EmployeeAdvances.Include(a => a.Installments.OrderBy(i => i.Number))
            .FirstOrDefaultAsync(a => a.Id == id);

    public async Task<EmployeeAdvance> SaveDraftAsync(EmployeeAdvance advance)
    {
        if (advance.Amount <= 0) throw new InvalidOperationException("Amount must be positive.");
        if (advance.InstallmentCount < 1) advance.InstallmentCount = 1;
        advance.MonthlyDeduction = Math.Round(advance.Amount / advance.InstallmentCount, 2);

        if (advance.Id == 0)
        {
            advance.AdvanceNo = $"ADV-{DateTime.Today.Year}-{await db.EmployeeAdvances.IgnoreQueryFilters().CountAsync() + 1:D5}";
            advance.EmployeeId = currentUser.UserId!;
            advance.EmployeeName = currentUser.UserName ?? "";
            db.EmployeeAdvances.Add(advance);
        }
        else
        {
            var existing = await db.EmployeeAdvances.FirstAsync(a => a.Id == advance.Id);
            if (existing.Status != AdvanceStatus.Draft) throw new InvalidOperationException("Only drafts can be edited.");
            existing.Amount = advance.Amount;
            existing.Reason = advance.Reason;
            existing.DueDate = advance.DueDate;
            existing.InstallmentCount = advance.InstallmentCount;
            existing.MonthlyDeduction = advance.MonthlyDeduction;
            advance = existing;
        }
        await db.SaveChangesAsync();
        return advance;
    }

    public async Task SubmitAsync(int id)
    {
        var a = await db.EmployeeAdvances.FirstAsync(x => x.Id == id);
        if (a.Status != AdvanceStatus.Draft) throw new InvalidOperationException("Already submitted.");
        a.Status = AdvanceStatus.PendingApproval;
        await db.SaveChangesAsync();
        await notifications.NotifyRoleAsync(AppRoles.FinanceManager, $"Advance approval: {a.AdvanceNo}",
            $"{a.EmployeeName} requested advance of {a.Amount:N2}", NotificationType.ApprovalRequest, $"/advances/{a.Id}");
    }

    public async Task ApproveAsync(int id)
    {
        var a = await db.EmployeeAdvances.FirstAsync(x => x.Id == id);
        if (a.Status != AdvanceStatus.PendingApproval) throw new InvalidOperationException("Not pending approval.");
        a.Status = AdvanceStatus.Approved;
        a.ApprovedBy = currentUser.UserName;
        a.ApprovedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await notifications.NotifyAsync(a.EmployeeId, $"Advance {a.AdvanceNo} approved", null,
            NotificationType.Approved, $"/advances/{a.Id}");
        await notifications.NotifyRoleAsync(AppRoles.Accountant, $"Disburse advance {a.AdvanceNo}",
            $"{a.EmployeeName} — {a.Amount:N2}", NotificationType.ApprovalRequest, $"/advances/{a.Id}");
    }

    public async Task RejectAsync(int id, string? reason)
    {
        var a = await db.EmployeeAdvances.FirstAsync(x => x.Id == id);
        if (a.Status != AdvanceStatus.PendingApproval) throw new InvalidOperationException("Not pending approval.");
        a.Status = AdvanceStatus.Rejected;
        await db.SaveChangesAsync();
        await notifications.NotifyAsync(a.EmployeeId, $"Advance {a.AdvanceNo} rejected", reason,
            NotificationType.Rejected, $"/advances/{a.Id}");
    }

    /// <summary>
    /// Disburse: Dr employee's advance sub-account, Cr cash/bank. Builds the installment schedule.
    /// </summary>
    public async Task<Voucher> DisburseAsync(int id, int payFromAccountId)
    {
        var a = await db.EmployeeAdvances.Include(x => x.Installments).FirstAsync(x => x.Id == id);
        if (a.Status != AdvanceStatus.Approved) throw new InvalidOperationException("Advance must be approved first.");

        var advAccount = await accountService.EnsureChildAccountAsync(AdvancesParentCode, a.EmployeeName);

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashPayment, DateOnly.FromDateTime(DateTime.Today),
            $"Advance {a.AdvanceNo} disbursed to {a.EmployeeName}", "Advance", a.Id,
            [
                (advAccount.Id, a.Amount, 0m, $"Advance to {a.EmployeeName}"),
                (payFromAccountId, 0m, a.Amount, $"Advance {a.AdvanceNo}")
            ]);

        a.Status = AdvanceStatus.Disbursed;
        a.DisbursementVoucherId = voucher.Id;

        var start = DateOnly.FromDateTime(DateTime.Today).AddMonths(1);
        var remaining = a.Amount;
        for (var i = 1; i <= a.InstallmentCount; i++)
        {
            var amount = i == a.InstallmentCount ? remaining : a.MonthlyDeduction;
            remaining -= amount;
            a.Installments.Add(new AdvanceInstallment
            {
                Number = i, DueDate = start.AddMonths(i - 1), Amount = amount
            });
        }
        await db.SaveChangesAsync();
        await notifications.NotifyAsync(a.EmployeeId, $"Advance {a.AdvanceNo} disbursed",
            $"Voucher {voucher.VoucherNo}; repayment starts {start:yyyy-MM-dd}", NotificationType.Info, $"/advances/{a.Id}");
        return voucher;
    }

    /// <summary>
    /// Repayment (e.g. salary deduction): Dr cash/salary-payable, Cr employee's advance sub-account.
    /// </summary>
    public async Task<Voucher> RepayInstallmentAsync(int installmentId, decimal amount, int receiveIntoAccountId, DateOnly date)
    {
        var inst = await db.AdvanceInstallments.Include(i => i.EmployeeAdvance).FirstAsync(i => i.Id == installmentId);
        var a = inst.EmployeeAdvance;
        if (amount <= 0 || amount > inst.Amount - inst.PaidAmount)
            throw new InvalidOperationException("Invalid repayment amount.");

        var advAccount = await accountService.EnsureChildAccountAsync(AdvancesParentCode, a.EmployeeName);

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashReceipt, date,
            $"Advance {a.AdvanceNo} installment #{inst.Number} repayment — {a.EmployeeName}", "Advance", a.Id,
            [
                (receiveIntoAccountId, amount, 0m, $"Repayment {a.AdvanceNo} #{inst.Number}"),
                (advAccount.Id, 0m, amount, $"Repayment {a.AdvanceNo} #{inst.Number}")
            ]);

        inst.PaidAmount += amount;
        inst.PaidDate = date;
        inst.RepaymentVoucherId = voucher.Id;
        inst.Status = inst.PaidAmount >= inst.Amount ? InstallmentStatus.Paid : InstallmentStatus.PartiallyPaid;

        a.RepaidAmount += amount;
        a.Status = a.RepaidAmount >= a.Amount ? AdvanceStatus.Settled : AdvanceStatus.Repaying;
        await db.SaveChangesAsync();
        return voucher;
    }
}
