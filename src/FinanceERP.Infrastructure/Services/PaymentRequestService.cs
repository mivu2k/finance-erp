using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Domain.Security;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class PaymentRequestService(
    AppDbContext db,
    ICurrentUserService currentUser,
    IVoucherService voucherService,
    IAccountService accountService,
    INotificationService notifications) : IPaymentRequestService
{
    private const string AdvancesParentCode = "1700"; // Employee Advances (Asset)

    public async Task<PagedResult<PaymentRequest>> ListAsync(ReportFilter f, string? requesterId = null, RequestStatus? status = null)
    {
        var q = db.PaymentRequests.AsNoTracking().Include(r => r.Department).AsQueryable();
        if (requesterId is not null) q = q.Where(r => r.RequesterId == requesterId);
        if (status is not null) q = q.Where(r => r.Status == status);
        if (f.From is not null) q = q.Where(r => r.CreatedAtUtc >= f.From.Value.ToDateTime(TimeOnly.MinValue));
        if (f.To is not null) q = q.Where(r => r.CreatedAtUtc <= f.To.Value.ToDateTime(TimeOnly.MaxValue));
        if (f.DepartmentId is not null) q = q.Where(r => r.DepartmentId == f.DepartmentId);
        if (!string.IsNullOrWhiteSpace(f.Search))
            q = q.Where(r => r.RequestNo.Contains(f.Search) || r.RequesterName.Contains(f.Search)
                || (r.Purpose != null && r.Purpose.Contains(f.Search)));

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        return new PagedResult<PaymentRequest>(items, total);
    }

    public Task<PaymentRequest?> GetAsync(int id) =>
        db.PaymentRequests
            .Include(r => r.Lines).ThenInclude(l => l.Account)
            .Include(r => r.Approvals)
            .Include(r => r.Department)
            .Include(r => r.Voucher)
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == id);

    public async Task<PaymentRequest> SaveDraftAsync(PaymentRequest request)
    {
        // Advance kind: the requester asks for a lump sum — TotalAmount is entered
        // directly and lines only appear later, at justification time.
        if (request.Kind != RequestKind.Advance)
            request.TotalAmount = request.Lines.Sum(l => l.Amount);
        if (request.Id == 0)
        {
            request.RequestNo = await NextNumberAsync(request.IsDirectorRequest ? "DFR" : "PR");
            request.RequesterId = currentUser.UserId!;
            request.RequesterName = currentUser.UserName ?? "";
            db.PaymentRequests.Add(request);
        }
        else
        {
            var existing = await db.PaymentRequests.Include(r => r.Lines).FirstAsync(r => r.Id == request.Id);
            EnsureOwn(existing);
            if (existing.Status != RequestStatus.Draft)
                throw new InvalidOperationException("Only draft requests can be edited.");
            existing.Purpose = request.Purpose;
            existing.DepartmentId = request.DepartmentId;
            existing.ProjectId = request.ProjectId;
            if (existing.Kind == RequestKind.Advance)
            {
                existing.TotalAmount = request.TotalAmount;
            }
            else
            {
                db.PaymentRequestLines.RemoveRange(existing.Lines);
                existing.Lines = request.Lines.Select((l, i) => new PaymentRequestLine
                {
                    AccountId = l.AccountId, Category = l.Category, Amount = l.Amount,
                    Reason = l.Reason, Description = l.Description, LineNo = i + 1,
                    AttachmentPath = l.AttachmentPath, AttachmentName = l.AttachmentName
                }).ToList();
                existing.TotalAmount = existing.Lines.Sum(l => l.Amount);
            }
            request = existing;
        }
        await db.SaveChangesAsync();
        return request;
    }

    public async Task SubmitAsync(int id)
    {
        var r = await db.PaymentRequests.Include(x => x.Lines).FirstAsync(x => x.Id == id);
        EnsureOwn(r);
        if (r.Status != RequestStatus.Draft) throw new InvalidOperationException("Request already submitted.");
        if (r.TotalAmount <= 0 || (r.Kind != RequestKind.Advance && r.Lines.Count == 0))
            throw new InvalidOperationException("Add at least one line with an amount.");

        // Director fund requests skip manager approval and go straight to Admin.
        r.Status = r.IsDirectorRequest ? RequestStatus.PendingAdmin : RequestStatus.PendingManager;
        AddTrail(r, "Requester", ApprovalAction.Submitted, null);
        await db.SaveChangesAsync();

        var targetRole = r.IsDirectorRequest ? AppRoles.Admin : AppRoles.Manager;
        await notifications.NotifyRoleAsync(targetRole, $"Approval needed: {r.RequestNo}",
            $"{r.RequesterName} requested {r.TotalAmount:N2} — {r.Purpose}",
            NotificationType.ApprovalRequest, $"/requests/{r.Id}");
    }

    public async Task ApproveAsync(int id, string level, string? comment)
    {
        var r = await db.PaymentRequests.FirstAsync(x => x.Id == id);
        switch (level)
        {
            case "Manager" when r.Status == RequestStatus.PendingManager:
                r.Status = RequestStatus.PendingAdmin;
                AddTrail(r, level, ApprovalAction.Approved, comment);
                await db.SaveChangesAsync();
                await notifications.NotifyRoleAsync(AppRoles.Admin, $"Approval needed: {r.RequestNo}",
                    $"{r.RequesterName} — {r.TotalAmount:N2}", NotificationType.ApprovalRequest, $"/requests/{r.Id}");
                break;
            case "Admin" when r.Status == RequestStatus.PendingAdmin:
                r.Status = RequestStatus.PendingAccountant;
                AddTrail(r, level, ApprovalAction.Approved, comment);
                await db.SaveChangesAsync();
                await notifications.NotifyRoleAsync(AppRoles.Accountant, $"Payment due: {r.RequestNo}",
                    $"{r.RequesterName} — {r.TotalAmount:N2}", NotificationType.ApprovalRequest, $"/requests/{r.Id}");
                break;
            default:
                throw new InvalidOperationException($"Request is not pending {level} approval.");
        }
        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} approved by {level}",
            comment, NotificationType.Approved, $"/requests/{r.Id}");
    }

    public async Task RejectAsync(int id, string level, string? comment)
    {
        var r = await db.PaymentRequests.FirstAsync(x => x.Id == id);
        if (r.Status is not (RequestStatus.PendingManager or RequestStatus.PendingAdmin or RequestStatus.PendingAccountant))
            throw new InvalidOperationException("Request is not pending approval.");
        r.Status = RequestStatus.Rejected;
        AddTrail(r, level, ApprovalAction.Rejected, comment);
        await db.SaveChangesAsync();
        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} rejected",
            comment, NotificationType.Rejected, $"/requests/{r.Id}");
    }

    /// <summary>
    /// Accountant pays: creates a posted payment voucher debiting each request line's
    /// expense account and crediting the cash/bank account, then marks the request Paid.
    /// </summary>
    public async Task<Voucher> PayAsync(int id, int payFromAccountId, string? comment,
        IReadOnlyDictionary<int, int>? lineAccounts = null)
    {
        var r = await db.PaymentRequests.Include(x => x.Lines).ThenInclude(l => l.Account).FirstAsync(x => x.Id == id);
        if (r.Status != RequestStatus.PendingAccountant)
            throw new InvalidOperationException("Request is not ready for payment.");
        if (r.Kind == RequestKind.Advance)
            throw new InvalidOperationException("Advance requests are disbursed, not paid — use Disburse.");

        // The accountant classifies each line to a ledger account before payment.
        if (lineAccounts is not null)
            foreach (var line in r.Lines)
                if (lineAccounts.TryGetValue(line.Id, out var accountId))
                    line.AccountId = accountId;

        if (r.Lines.Any(l => l.AccountId is null))
            throw new InvalidOperationException("Assign an account head to every line before paying.");

        var lines = r.Lines
            .Select(l => (AccountId: l.AccountId!.Value, Debit: l.Amount, Credit: 0m,
                Description: (string?)($"{r.RequestNo} — {l.Reason ?? l.Description ?? l.Category}")))
            .ToList();
        lines.Add((payFromAccountId, 0m, r.TotalAmount, $"{r.RequestNo} paid to {r.RequesterName}"));

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashPayment, DateOnly.FromDateTime(DateTime.Today),
            $"Payment request {r.RequestNo} — {r.RequesterName}: {r.Purpose}",
            r.IsDirectorRequest ? "DirectorFund" : "PaymentRequest", r.Id, lines);

        // Stamp the request's project/department onto the ledger lines so
        // project- and department-filtered reports include this payment.
        foreach (var vl in voucher.Lines)
        {
            vl.ProjectId = r.ProjectId;
            vl.DepartmentId = r.DepartmentId;
        }

        r.Status = RequestStatus.Paid;
        r.VoucherId = voucher.Id;
        AddTrail(r, "Accountant", ApprovalAction.Paid, comment);
        await db.SaveChangesAsync();

        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} paid",
            $"Voucher {voucher.VoucherNo}", NotificationType.Approved, $"/requests/{r.Id}");
        return voucher;
    }

    /// <summary>
    /// Advance kind, accountant step 1: hand over the lump sum.
    /// Dr the requester's advance sub-account (company's claim), Cr cash/bank.
    /// </summary>
    public async Task<Voucher> DisburseAsync(int id, int payFromAccountId, string? comment)
    {
        var r = await db.PaymentRequests.FirstAsync(x => x.Id == id);
        if (r.Kind != RequestKind.Advance) throw new InvalidOperationException("Only advance requests are disbursed.");
        if (r.Status != RequestStatus.PendingAccountant) throw new InvalidOperationException("Request is not ready for disbursement.");

        var advAccount = await accountService.EnsureChildAccountAsync(AdvancesParentCode, r.RequesterName);
        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashPayment, DateOnly.FromDateTime(DateTime.Today),
            $"Advance {r.RequestNo} disbursed to {r.RequesterName} — {r.Purpose}",
            "PaymentRequest", r.Id,
            [
                (advAccount.Id, r.TotalAmount, 0m, $"{r.RequestNo} advance to {r.RequesterName}"),
                (payFromAccountId, 0m, r.TotalAmount, $"{r.RequestNo} advance")
            ]);

        r.Status = RequestStatus.Disbursed;
        r.VoucherId = voucher.Id;
        r.AdvanceAccountId = advAccount.Id;
        AddTrail(r, "Accountant", ApprovalAction.Paid, comment ?? "Advance disbursed");
        await db.SaveChangesAsync();

        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} disbursed",
            $"You received {r.TotalAmount:N2}. Submit your expense justification when you're back.",
            NotificationType.Approved, $"/requests/{r.Id}");
        return voucher;
    }

    /// <summary>Advance kind: requester reports actual spend by category after the trip.</summary>
    public async Task SubmitJustificationAsync(int id, List<PaymentRequestLine> lines)
    {
        var r = await db.PaymentRequests.Include(x => x.Lines).FirstAsync(x => x.Id == id);
        EnsureOwn(r);
        if (r.Status != RequestStatus.Disbursed) throw new InvalidOperationException("Justification can only be submitted after disbursement.");
        lines = lines.Where(l => l.Amount > 0).ToList();
        if (lines.Count == 0) throw new InvalidOperationException("Add at least one expense line.");

        db.PaymentRequestLines.RemoveRange(r.Lines);
        r.Lines = lines.Select((l, i) => new PaymentRequestLine
        {
            Category = l.Category, Amount = l.Amount, Reason = l.Reason,
            Description = l.Description, LineNo = i + 1,
            AttachmentPath = l.AttachmentPath, AttachmentName = l.AttachmentName
        }).ToList();
        r.Status = RequestStatus.JustificationPending;
        AddTrail(r, "Requester", ApprovalAction.Submitted, $"Justified {r.Lines.Sum(l => l.Amount):N2} of {r.TotalAmount:N2}");
        await db.SaveChangesAsync();

        await notifications.NotifyRoleAsync(AppRoles.Admin, $"Justification review: {r.RequestNo}",
            $"{r.RequesterName} justified {r.Lines.Sum(l => l.Amount):N2} against an advance of {r.TotalAmount:N2}",
            NotificationType.ApprovalRequest, $"/requests/{r.Id}");
    }

    public async Task ApproveJustificationAsync(int id, string? comment)
    {
        var r = await db.PaymentRequests.FirstAsync(x => x.Id == id);
        if (r.Status != RequestStatus.JustificationPending) throw new InvalidOperationException("No justification pending.");
        r.Status = RequestStatus.SettlementReady;
        AddTrail(r, "Admin", ApprovalAction.Approved, comment);
        await db.SaveChangesAsync();
        await notifications.NotifyRoleAsync(AppRoles.Accountant, $"Settle advance: {r.RequestNo}",
            $"{r.RequesterName} — justification approved", NotificationType.ApprovalRequest, $"/requests/{r.Id}");
        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} justification approved", comment,
            NotificationType.Approved, $"/requests/{r.Id}");
    }

    /// <summary>Sends the justification back to the requester for rework (stays Disbursed).</summary>
    public async Task RejectJustificationAsync(int id, string? comment)
    {
        var r = await db.PaymentRequests.FirstAsync(x => x.Id == id);
        if (r.Status != RequestStatus.JustificationPending) throw new InvalidOperationException("No justification pending.");
        r.Status = RequestStatus.Disbursed;
        AddTrail(r, "Admin", ApprovalAction.Rejected, comment);
        await db.SaveChangesAsync();
        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} justification returned",
            comment ?? "Please revise and resubmit your expense justification.",
            NotificationType.Rejected, $"/requests/{r.Id}");
    }

    /// <summary>
    /// Advance kind, accountant step 2: post actuals and clear the advance.
    /// Dr each expense line (actual), Cr advance sub-account (disbursed amount);
    /// the difference is settled in cash — Dr cash if the employee returns unspent
    /// money, Cr cash if the company reimburses overspend.
    /// </summary>
    public async Task<Voucher> SettleAsync(int id, int cashAccountId, string? comment,
        IReadOnlyDictionary<int, int> lineAccounts)
    {
        var r = await db.PaymentRequests.Include(x => x.Lines).FirstAsync(x => x.Id == id);
        if (r.Status != RequestStatus.SettlementReady) throw new InvalidOperationException("Justification must be approved before settlement.");
        if (r.AdvanceAccountId is null) throw new InvalidOperationException("Advance account missing.");

        foreach (var line in r.Lines)
            if (lineAccounts.TryGetValue(line.Id, out var accountId))
                line.AccountId = accountId;
        if (r.Lines.Any(l => l.AccountId is null))
            throw new InvalidOperationException("Assign an account head to every line before settling.");

        var actual = r.Lines.Sum(l => l.Amount);
        var difference = r.TotalAmount - actual; // >0: employee returns cash; <0: company reimburses

        var lines = r.Lines
            .Select(l => (AccountId: l.AccountId!.Value, Debit: l.Amount, Credit: 0m,
                Description: (string?)($"{r.RequestNo} — {l.Category}: {l.Reason}")))
            .ToList();
        lines.Add((r.AdvanceAccountId.Value, 0m, r.TotalAmount, $"{r.RequestNo} advance cleared"));
        if (difference > 0)
            lines.Add((cashAccountId, difference, 0m, $"{r.RequestNo} unspent advance returned by {r.RequesterName}"));
        else if (difference < 0)
            lines.Add((cashAccountId, 0m, -difference, $"{r.RequestNo} overspend reimbursed to {r.RequesterName}"));

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.Journal, DateOnly.FromDateTime(DateTime.Today),
            $"Settlement {r.RequestNo} — {r.RequesterName}: actual {actual:N2} vs advance {r.TotalAmount:N2}",
            "PaymentRequest", r.Id, lines);

        foreach (var vl in voucher.Lines)
        {
            vl.ProjectId = r.ProjectId;
            vl.DepartmentId = r.DepartmentId;
        }

        r.Status = RequestStatus.Settled;
        r.SettlementVoucherId = voucher.Id;
        AddTrail(r, "Accountant", ApprovalAction.Paid,
            comment ?? (difference > 0 ? $"Returned {difference:N2}" : difference < 0 ? $"Reimbursed {-difference:N2}" : "Fully utilised"));
        await db.SaveChangesAsync();

        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} settled",
            $"Voucher {voucher.VoucherNo}. " + (difference > 0
                ? $"Please return {difference:N2}."
                : difference < 0 ? $"You will be reimbursed {-difference:N2}." : "Advance fully utilised."),
            NotificationType.Approved, $"/requests/{r.Id}");
        return voucher;
    }

    public async Task CancelAsync(int id)
    {
        var r = await db.PaymentRequests.FirstAsync(x => x.Id == id);
        EnsureOwn(r);
        if (r.Status is RequestStatus.Paid) throw new InvalidOperationException("Paid requests cannot be cancelled.");
        r.Status = RequestStatus.Cancelled;
        AddTrail(r, "Requester", ApprovalAction.Cancelled, null);
        await db.SaveChangesAsync();
    }

    private void EnsureOwn(PaymentRequest r)
    {
        if (r.RequesterId != currentUser.UserId)
            throw new UnauthorizedAccessException("You can only modify your own requests.");
    }

    private void AddTrail(PaymentRequest r, string level, ApprovalAction action, string? comment) =>
        r.Approvals.Add(new RequestApproval
        {
            Level = level, Action = action, Comment = comment,
            ActorId = currentUser.UserId ?? "", ActorName = currentUser.UserName ?? ""
        });

    private async Task<string> NextNumberAsync(string prefix)
    {
        var year = DateTime.Today.Year;
        var count = await db.PaymentRequests.IgnoreQueryFilters()
            .CountAsync(r => r.CreatedAtUtc.Year == year && r.RequestNo.StartsWith(prefix));
        return $"{prefix}-{year}-{count + 1:D5}";
    }
}
