using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Domain.Security;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Web.Services;

/// <summary>
/// Periodic checks: overdue advance/loan installments and low cash balance.
/// Marks overdue installments and raises notifications. Runs every 6 hours;
/// each alert is sent at most once per day (guarded by an existing-notification check).
/// </summary>
public class AlertsBackgroundService(IServiceScopeFactory scopeFactory, ILogger<AlertsBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup migration/seeding finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunChecksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alert checks failed");
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var since = DateTime.UtcNow.AddDays(-1);

        async Task<bool> AlreadySentToday(string title) =>
            await db.Notifications.AnyAsync(n => n.Title == title && n.CreatedAtUtc >= since, ct);

        // Overdue advance installments
        var overdueAdvances = await db.AdvanceInstallments
            .Include(i => i.EmployeeAdvance)
            .Where(i => i.Status != InstallmentStatus.Paid && i.DueDate < today
                && !i.EmployeeAdvance.IsDeleted
                && (i.EmployeeAdvance.Status == AdvanceStatus.Disbursed || i.EmployeeAdvance.Status == AdvanceStatus.Repaying))
            .ToListAsync(ct);
        foreach (var inst in overdueAdvances)
        {
            if (inst.Status != InstallmentStatus.Overdue)
            {
                inst.Status = InstallmentStatus.Overdue;
            }
            var title = $"Advance {inst.EmployeeAdvance.AdvanceNo} installment #{inst.Number} overdue";
            if (!await AlreadySentToday(title))
            {
                var msg = $"Due {inst.DueDate:yyyy-MM-dd}, {inst.Amount - inst.PaidAmount:N2} outstanding";
                await notifications.NotifyAsync(inst.EmployeeAdvance.EmployeeId, title, msg,
                    NotificationType.AdvanceDue, $"/advances/{inst.EmployeeAdvanceId}");
                await notifications.NotifyRoleAsync(AppRoles.Accountant, title, msg,
                    NotificationType.AdvanceDue, $"/advances/{inst.EmployeeAdvanceId}");
            }
        }

        // Overdue loan installments
        var overdueLoans = await db.LoanInstallments
            .Include(i => i.Loan).ThenInclude(l => l.ThirdParty)
            .Where(i => i.Status != InstallmentStatus.Paid && i.DueDate < today
                && !i.Loan.IsDeleted && i.Loan.Status == LoanStatus.Active)
            .ToListAsync(ct);
        foreach (var inst in overdueLoans)
        {
            if (inst.Status != InstallmentStatus.Overdue) inst.Status = InstallmentStatus.Overdue;
            var title = $"Loan {inst.Loan.LoanNo} installment #{inst.Number} overdue";
            if (!await AlreadySentToday(title))
            {
                await notifications.NotifyRoleAsync(AppRoles.FinanceManager, title,
                    $"{inst.Loan.ThirdParty.Name} — due {inst.DueDate:yyyy-MM-dd}, {inst.Amount - inst.PaidAmount:N2} outstanding",
                    NotificationType.LoanDue, "/loans");
            }
        }
        await db.SaveChangesAsync(ct);

        // Utility bills due within 3 days or overdue
        var dueSoon = today.AddDays(3);
        var dueBills = await db.UtilityBills
            .Include(b => b.Connection).ThenInclude(c => c.Location)
            .Where(b => b.VoucherId == null && b.DueDate != null && b.DueDate <= dueSoon)
            .ToListAsync(ct);
        foreach (var bill in dueBills)
        {
            var overdue = bill.DueDate < today;
            var title = $"Utility bill {(overdue ? "overdue" : "due")}: {bill.Connection.Location.Name} — {bill.Connection.Name} ({bill.BillMonth:MMM yyyy})";
            if (!await AlreadySentToday(title))
                await notifications.NotifyRoleAsync(AppRoles.Accountant, title,
                    $"{bill.Amount:N2} due {bill.DueDate:yyyy-MM-dd}" +
                    (bill.Connection.ConsumerNumber is null ? "" : $" · Consumer # {bill.Connection.ConsumerNumber}"),
                    NotificationType.PaymentDue, "/utilities");
        }

        // Low cash
        var thresholdSetting = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.LowCashThreshold, ct);
        if (decimal.TryParse(thresholdSetting?.Value, out var threshold) && threshold > 0)
        {
            var cash = await db.VoucherLines.AsNoTracking()
                .Where(l => l.Account.Code == "1100" && l.Voucher.Status == VoucherStatus.Posted)
                .GroupBy(_ => 1).Select(g => g.Sum(x => x.Debit) - g.Sum(x => x.Credit))
                .FirstOrDefaultAsync(ct);
            var title = "Low cash balance alert";
            if (cash < threshold && !await AlreadySentToday(title))
            {
                var msg = $"Cash in hand is {cash:N2}, below the threshold of {threshold:N2}.";
                await notifications.NotifyRoleAsync(AppRoles.Accountant, title, msg, NotificationType.LowCash, "/petty-cash");
                await notifications.NotifyRoleAsync(AppRoles.Director, title, msg, NotificationType.LowCash, "/reports");
            }
        }

        logger.LogInformation("Alert checks completed: {Advances} overdue advances, {Loans} overdue loans",
            overdueAdvances.Count, overdueLoans.Count);
    }
}
