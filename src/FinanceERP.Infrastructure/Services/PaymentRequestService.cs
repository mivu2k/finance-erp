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
    INotificationService notifications) : IPaymentRequestService
{
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
            .FirstOrDefaultAsync(r => r.Id == id);

    public async Task<PaymentRequest> SaveDraftAsync(PaymentRequest request)
    {
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
            db.PaymentRequestLines.RemoveRange(existing.Lines);
            existing.Lines = request.Lines.Select((l, i) => new PaymentRequestLine
            {
                AccountId = l.AccountId, Category = l.Category, Amount = l.Amount,
                Reason = l.Reason, Description = l.Description, LineNo = i + 1
            }).ToList();
            existing.TotalAmount = existing.Lines.Sum(l => l.Amount);
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
        if (r.Lines.Count == 0 || r.TotalAmount <= 0) throw new InvalidOperationException("Add at least one line with an amount.");

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
    public async Task<Voucher> PayAsync(int id, int payFromAccountId, string? comment)
    {
        var r = await db.PaymentRequests.Include(x => x.Lines).ThenInclude(l => l.Account).FirstAsync(x => x.Id == id);
        if (r.Status != RequestStatus.PendingAccountant)
            throw new InvalidOperationException("Request is not ready for payment.");

        var lines = r.Lines
            .Select(l => (l.AccountId, Debit: l.Amount, Credit: 0m,
                Description: (string?)($"{r.RequestNo} — {l.Reason ?? l.Description ?? l.Category}")))
            .ToList();
        lines.Add((payFromAccountId, 0m, r.TotalAmount, $"{r.RequestNo} paid to {r.RequesterName}"));

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashPayment, DateOnly.FromDateTime(DateTime.Today),
            $"Payment request {r.RequestNo} — {r.RequesterName}: {r.Purpose}",
            r.IsDirectorRequest ? "DirectorFund" : "PaymentRequest", r.Id, lines);

        r.Status = RequestStatus.Paid;
        r.VoucherId = voucher.Id;
        AddTrail(r, "Accountant", ApprovalAction.Paid, comment);
        await db.SaveChangesAsync();

        await notifications.NotifyAsync(r.RequesterId, $"{r.RequestNo} paid",
            $"Voucher {voucher.VoucherNo}", NotificationType.Approved, $"/requests/{r.Id}");
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
