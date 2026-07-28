using ErpPlatform.Shared.Identity;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

/// <summary>
/// Notifications live in the accounts database but are addressed to platform
/// users, so role membership and e-mail addresses are read through the identity
/// directory rather than joined to.
/// </summary>
public class NotificationService(AppDbContext db, IAppEmailSender email, IPlatformUserDirectory directory)
    : INotificationService
{
    public async Task NotifyAsync(string userId, string title, string? message, NotificationType type, string? link = null)
    {
        db.Notifications.Add(new Notification
        {
            UserId = userId, Title = title, Message = message, Type = type, Link = link,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await EmailUsersAsync([userId], title, message);
    }

    public async Task NotifyRoleAsync(string roleName, string title, string? message, NotificationType type, string? link = null)
    {
        var userIds = (await directory.ListByRoleAsync(roleName)).Select(u => u.UserId).ToList();
        var now = DateTime.UtcNow;
        db.Notifications.AddRange(userIds.Select(id => new Notification
        {
            UserId = id, Title = title, Message = message, Type = type, Link = link, CreatedAtUtc = now
        }));
        await db.SaveChangesAsync();
        await EmailUsersAsync(userIds, title, message);
    }

    private async Task EmailUsersAsync(IEnumerable<string> userIds, string title, string? message)
    {
        if (!email.Enabled) return;
        var addresses = (await directory.ListByIdsAsync(userIds))
            .Where(u => u.Email is not null).Select(u => u.Email!).ToList();
        foreach (var address in addresses)
            await email.SendAsync(address, title, message ?? title);
    }

    public Task<List<Notification>> GetUnreadAsync(string userId, int max = 20) =>
        db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId && !n.IsRead)
            .OrderByDescending(n => n.Id).Take(max).ToListAsync();

    public async Task MarkReadAsync(int id)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id);
        if (n is null) return;
        n.IsRead = true;
        await db.SaveChangesAsync();
    }

    public async Task MarkAllReadAsync(string userId)
    {
        await db.Notifications.Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
    }
}
