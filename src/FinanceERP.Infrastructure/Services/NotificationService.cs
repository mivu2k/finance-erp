using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class NotificationService(AppDbContext db) : INotificationService
{
    public async Task NotifyAsync(string userId, string title, string? message, NotificationType type, string? link = null)
    {
        db.Notifications.Add(new Notification
        {
            UserId = userId, Title = title, Message = message, Type = type, Link = link,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task NotifyRoleAsync(string roleName, string title, string? message, NotificationType type, string? link = null)
    {
        var userIds = await (
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.Id
            where r.Name == roleName
            select ur.UserId).ToListAsync();
        var now = DateTime.UtcNow;
        db.Notifications.AddRange(userIds.Select(id => new Notification
        {
            UserId = id, Title = title, Message = message, Type = type, Link = link, CreatedAtUtc = now
        }));
        await db.SaveChangesAsync();
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
