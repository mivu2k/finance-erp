using Microsoft.EntityFrameworkCore;
using Tender.Domain;

namespace Tender.Infrastructure;

/// <summary>
/// Tasks for both registers. Bid preparation is as much a checklist with owners and
/// deadlines as project delivery is, so the same board, service and rules serve both
/// rather than a second near-identical copy that drifts.
/// </summary>
public interface IWorkTaskService
{
    Task<List<WorkTask>> ListForAsync(WorkOwnerType ownerType, int ownerId,
        CancellationToken ct = default);

    Task<WorkTask?> GetAsync(int id, CancellationToken ct = default);

    Task<WorkTask> AddAsync(WorkOwnerType ownerType, int ownerId, WorkTask task,
        CancellationToken ct = default);

    Task<WorkTask> UpdateAsync(WorkTask task, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Moves a task to a status from the board without touching the rest of the row —
    /// what a team member holding only <c>tender.tasks.manage</c> is allowed to do.
    /// </summary>
    Task<WorkTask> SetStatusAsync(int id, ProjectTaskStatus status, CancellationToken ct = default);

    /// <summary>Open tasks already past their due date, across both registers.</summary>
    Task<List<WorkTask>> ListOverdueAsync(CancellationToken ct = default);

    /// <summary>Open tasks due within the window — what a dashboard flags as coming up.</summary>
    Task<List<WorkTask>> ListUpcomingAsync(int withinDays = 14, CancellationToken ct = default);

    /// <summary>Every open task assigned to one person — their work list, both registers.</summary>
    Task<List<WorkTask>> ListForUserAsync(string userId, CancellationToken ct = default);
}

public class WorkTaskService(TenderDbContext db) : IWorkTaskService
{
    private static readonly List<ProjectTaskStatus> OpenStatuses =
    [
        ProjectTaskStatus.NotStarted, ProjectTaskStatus.InProgress, ProjectTaskStatus.Blocked
    ];

    public async Task<List<WorkTask>> ListForAsync(
        WorkOwnerType ownerType, int ownerId, CancellationToken ct = default) =>
        await Owned(ownerType, ownerId).AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(ct);

    private IQueryable<WorkTask> Owned(WorkOwnerType ownerType, int ownerId) =>
        ownerType == WorkOwnerType.Tender
            ? db.WorkTasks.Where(t => t.TenderRecordId == ownerId)
            : db.WorkTasks.Where(t => t.ProjectId == ownerId);

    public Task<WorkTask?> GetAsync(int id, CancellationToken ct = default) =>
        db.WorkTasks.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<WorkTask> AddAsync(
        WorkOwnerType ownerType, int ownerId, WorkTask task, CancellationToken ct = default)
    {
        Validate(task);

        if (ownerType == WorkOwnerType.Tender)
        {
            if (!await db.Tenders.AnyAsync(t => t.Id == ownerId, ct))
                throw new InvalidOperationException("Tender not found.");
            task.TenderRecordId = ownerId;
            task.ProjectId = null;
        }
        else
        {
            if (!await db.Projects.AnyAsync(p => p.Id == ownerId, ct))
                throw new InvalidOperationException("Project not found.");
            task.ProjectId = ownerId;
            task.TenderRecordId = null;
        }

        if (task.SortOrder == 0)
        {
            var last = await Owned(ownerType, ownerId).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
            task.SortOrder = last + 1;
        }

        Reconcile(task);
        db.WorkTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<WorkTask> UpdateAsync(WorkTask task, CancellationToken ct = default)
    {
        var existing = await db.WorkTasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct)
            ?? throw new InvalidOperationException("Task not found.");

        Validate(task);

        // Ownership is set once, at creation. Re-homing a task between a tender and a
        // project would silently move it off someone's board.
        existing.Title = task.Title;
        existing.Description = task.Description;
        existing.AssignedToUserId = task.AssignedToUserId;
        existing.AssignedToName = task.AssignedToName;
        existing.StartDate = task.StartDate;
        existing.DueDate = task.DueDate;
        existing.CompletedDate = task.CompletedDate;
        existing.Status = task.Status;
        existing.Priority = task.Priority;
        existing.ProgressPercent = task.ProgressPercent;
        existing.EstimatedHours = task.EstimatedHours;
        existing.ActualHours = task.ActualHours;
        existing.SortOrder = task.SortOrder;
        existing.Notes = task.Notes;

        Reconcile(existing);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<WorkTask> SetStatusAsync(
        int id, ProjectTaskStatus status, CancellationToken ct = default)
    {
        var task = await db.WorkTasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException("Task not found.");

        task.Status = status;
        Reconcile(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var task = await db.WorkTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return;
        db.WorkTasks.Remove(task);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Keeps the three things that describe "done" from contradicting each other:
    /// completing a task fills in its progress and completion date, and re-opening one
    /// clears the date so a finished-then-reopened task doesn't still read as delivered.
    /// </summary>
    private static void Reconcile(WorkTask task)
    {
        if (task.Status == ProjectTaskStatus.Completed)
        {
            task.ProgressPercent = 100;
            task.CompletedDate ??= DateOnly.FromDateTime(DateTime.UtcNow);
        }
        else
        {
            task.CompletedDate = null;
            if (task.ProgressPercent >= 100) task.ProgressPercent = 99;
            if (task.Status == ProjectTaskStatus.NotStarted && task.ProgressPercent > 0)
                task.Status = ProjectTaskStatus.InProgress;
        }
    }

    private static void Validate(WorkTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException("Task title is required.");
        if (task.ProgressPercent is < 0 or > 100)
            throw new InvalidOperationException("Progress must be between 0 and 100.");
        if (task.DueDate is { } due && task.StartDate is { } start && due < start)
            throw new InvalidOperationException("Due date cannot be before the start date.");
    }

    public async Task<List<WorkTask>> ListOverdueAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await OpenOnLiveOwners()
            .Where(t => t.DueDate != null && t.DueDate < today)
            .OrderBy(t => t.DueDate)
            .ToListAsync(ct);
    }

    public async Task<List<WorkTask>> ListUpcomingAsync(
        int withinDays = 14, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(withinDays);
        return await OpenOnLiveOwners()
            .Where(t => t.DueDate != null && t.DueDate >= today && t.DueDate <= cutoff)
            .OrderBy(t => t.DueDate)
            .ToListAsync(ct);
    }

    public async Task<List<WorkTask>> ListForUserAsync(string userId, CancellationToken ct = default) =>
        await OpenOnLiveOwners()
            .Where(t => t.AssignedToUserId == userId)
            .OrderBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ToListAsync(ct);

    /// <summary>
    /// Open tasks whose owner is still live — a task on a closed project or a lost
    /// tender is nobody's problem and shouldn't be chased.
    /// </summary>
    private IQueryable<WorkTask> OpenOnLiveOwners() =>
        db.WorkTasks.Include(t => t.Project).Include(t => t.TenderRecord).AsNoTracking()
            .Where(t => OpenStatuses.Contains(t.Status)
                        && (t.Project == null
                            || t.Project.Status == ProjectStatus.Planned
                            || t.Project.Status == ProjectStatus.Active
                            || t.Project.Status == ProjectStatus.OnHold)
                        && (t.TenderRecord == null
                            || t.TenderRecord.Status == TenderStatus.Identified
                            || t.TenderRecord.Status == TenderStatus.InPreparation
                            || t.TenderRecord.Status == TenderStatus.Submitted
                            || t.TenderRecord.Status == TenderStatus.TechnicallyQualified
                            || t.TenderRecord.Status == TenderStatus.Won));
}
