using Microsoft.EntityFrameworkCore;
using Tender.Domain;

namespace Tender.Infrastructure;

public interface IProjectService
{
    Task<List<Project>> ListAsync(string? search = null, ProjectStatus? status = null, CancellationToken ct = default);
    Task<Project?> GetAsync(int id, CancellationToken ct = default);
    Task<Project> CreateAsync(Project project, CancellationToken ct = default);
    Task<Project> UpdateAsync(Project project, CancellationToken ct = default);

    /// <summary>Soft-deletes a project together with its tasks and milestones.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task<ProjectTask> AddTaskAsync(int projectId, ProjectTask task, CancellationToken ct = default);
    Task<ProjectTask> UpdateTaskAsync(ProjectTask task, CancellationToken ct = default);
    Task DeleteTaskAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Moves a task to a status from the board without touching the rest of the row —
    /// what a team member holding only <c>tender.tasks.manage</c> is allowed to do.
    /// </summary>
    Task<ProjectTask> SetTaskStatusAsync(int id, ProjectTaskStatus status, CancellationToken ct = default);

    Task<ProjectMilestone> AddMilestoneAsync(int projectId, ProjectMilestone milestone, CancellationToken ct = default);
    Task<ProjectMilestone> UpdateMilestoneAsync(ProjectMilestone milestone, CancellationToken ct = default);
    Task DeleteMilestoneAsync(int id, CancellationToken ct = default);

    /// <summary>Open tasks already past their due date, across every open project.</summary>
    Task<List<ProjectTask>> ListOverdueTasksAsync(CancellationToken ct = default);

    /// <summary>Open tasks due within the window — what the dashboard flags as coming up.</summary>
    Task<List<ProjectTask>> ListUpcomingTasksAsync(int withinDays = 14, CancellationToken ct = default);

    /// <summary>Pending milestones due within the window.</summary>
    Task<List<ProjectMilestone>> ListUpcomingMilestonesAsync(int withinDays = 30, CancellationToken ct = default);

    /// <summary>Every open task assigned to one person, newest deadline first — their work list.</summary>
    Task<List<ProjectTask>> ListTasksForUserAsync(string userId, CancellationToken ct = default);
}

public class ProjectService(TenderDbContext db, IFileRegistryService files) : IProjectService
{
    private static readonly List<ProjectStatus> OpenStatuses =
    [
        ProjectStatus.Planned, ProjectStatus.Active, ProjectStatus.OnHold
    ];

    private static readonly List<ProjectTaskStatus> OpenTaskStatuses =
    [
        ProjectTaskStatus.NotStarted, ProjectTaskStatus.InProgress, ProjectTaskStatus.Blocked
    ];

    public async Task<List<Project>> ListAsync(
        string? search = null, ProjectStatus? status = null, CancellationToken ct = default)
    {
        var q = db.Projects.Include(p => p.Tasks).Include(p => p.Milestones)
            .AsNoTracking().AsSplitQuery().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(p => p.ProjectCode.Contains(s) || p.Name.Contains(s)
                          || (p.Client != null && p.Client.Contains(s))
                          || (p.ManagerName != null && p.ManagerName.Contains(s)));
        }

        if (status is { } st) q = q.Where(p => p.Status == st);

        return await q.OrderByDescending(p => p.StartDate ?? DateOnly.MinValue)
            .ThenByDescending(p => p.Id)
            .ToListAsync(ct);
    }

    public Task<Project?> GetAsync(int id, CancellationToken ct = default) =>
        db.Projects
            .Include(p => p.Tasks.OrderBy(t => t.SortOrder).ThenBy(t => t.Id))
            .Include(p => p.Milestones.OrderBy(m => m.SortOrder).ThenBy(m => m.DueDate))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Project> CreateAsync(Project project, CancellationToken ct = default)
    {
        Validate(project);

        if (await db.Projects.AnyAsync(p => p.ProjectCode == project.ProjectCode, ct))
            throw new InvalidOperationException("A project with this code already exists.");

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        // Every project gets its physical file the moment it exists, so a folder is
        // never created without a number and a sticker to put on it.
        await files.EnsureForAsync(FileOwnerType.Project, project.Id, project.ProjectCode, project.Name, ct);

        return project;
    }

    public async Task<Project> UpdateAsync(Project project, CancellationToken ct = default)
    {
        var existing = await db.Projects.FirstOrDefaultAsync(p => p.Id == project.Id, ct)
            ?? throw new InvalidOperationException("Project not found.");

        Validate(project);

        if (await db.Projects.AnyAsync(p => p.ProjectCode == project.ProjectCode && p.Id != project.Id, ct))
            throw new InvalidOperationException($"{project.ProjectCode} is already used by another project.");

        existing.ProjectCode = project.ProjectCode;
        existing.Name = project.Name;
        existing.Client = project.Client;
        existing.Description = project.Description;
        existing.Location = project.Location;
        existing.ManagerUserId = project.ManagerUserId;
        existing.ManagerName = project.ManagerName;
        existing.ContractValue = project.ContractValue;
        existing.Budget = project.Budget;
        existing.StartDate = project.StartDate;
        existing.TargetEndDate = project.TargetEndDate;
        existing.ActualEndDate = project.ActualEndDate;
        existing.Status = project.Status;
        existing.Priority = project.Priority;
        existing.ContactPerson = project.ContactPerson;
        existing.ContactPhone = project.ContactPhone;
        existing.ContactEmail = project.ContactEmail;
        existing.Notes = project.Notes;

        await db.SaveChangesAsync(ct);

        // Keeps the registry's snapshot in step with a rename.
        await files.EnsureForAsync(FileOwnerType.Project, existing.Id, existing.ProjectCode, existing.Name, ct);

        return existing;
    }

    private static void Validate(Project project)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectCode))
            throw new InvalidOperationException("Project code is required.");
        if (string.IsNullOrWhiteSpace(project.Name))
            throw new InvalidOperationException("Project name is required.");
        if (project.TargetEndDate is { } end && project.StartDate is { } start && end < start)
            throw new InvalidOperationException("Target end date cannot be before the start date.");
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var project = await db.Projects
            .Include(p => p.Tasks).Include(p => p.Milestones)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return;

        db.ProjectTasks.RemoveRange(project.Tasks);
        db.ProjectMilestones.RemoveRange(project.Milestones);
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProjectTask> AddTaskAsync(int projectId, ProjectTask task, CancellationToken ct = default)
    {
        ValidateTask(task);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new InvalidOperationException("Project not found.");

        task.ProjectId = project.Id;

        if (task.SortOrder == 0)
        {
            var last = await db.ProjectTasks.Where(t => t.ProjectId == project.Id)
                .MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
            task.SortOrder = last + 1;
        }

        Reconcile(task);
        db.ProjectTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<ProjectTask> UpdateTaskAsync(ProjectTask task, CancellationToken ct = default)
    {
        var existing = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct)
            ?? throw new InvalidOperationException("Task not found.");

        ValidateTask(task);

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

    public async Task<ProjectTask> SetTaskStatusAsync(
        int id, ProjectTaskStatus status, CancellationToken ct = default)
    {
        var task = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException("Task not found.");

        task.Status = status;
        Reconcile(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    /// <summary>
    /// Keeps the three things that describe "done" from contradicting each other:
    /// completing a task fills in its progress and completion date, and re-opening one
    /// clears the date so a finished-then-reopened task doesn't still read as delivered.
    /// </summary>
    private static void Reconcile(ProjectTask task)
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

    private static void ValidateTask(ProjectTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException("Task title is required.");
        if (task.ProgressPercent is < 0 or > 100)
            throw new InvalidOperationException("Progress must be between 0 and 100.");
        if (task.DueDate is { } due && task.StartDate is { } start && due < start)
            throw new InvalidOperationException("Due date cannot be before the start date.");
    }

    public async Task DeleteTaskAsync(int id, CancellationToken ct = default)
    {
        var task = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return;
        db.ProjectTasks.Remove(task);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProjectMilestone> AddMilestoneAsync(
        int projectId, ProjectMilestone milestone, CancellationToken ct = default)
    {
        ValidateMilestone(milestone);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new InvalidOperationException("Project not found.");

        milestone.ProjectId = project.Id;

        if (milestone.SortOrder == 0)
        {
            var last = await db.ProjectMilestones.Where(m => m.ProjectId == project.Id)
                .MaxAsync(m => (int?)m.SortOrder, ct) ?? 0;
            milestone.SortOrder = last + 1;
        }

        Reconcile(milestone);
        db.ProjectMilestones.Add(milestone);
        await db.SaveChangesAsync(ct);
        return milestone;
    }

    public async Task<ProjectMilestone> UpdateMilestoneAsync(
        ProjectMilestone milestone, CancellationToken ct = default)
    {
        var existing = await db.ProjectMilestones.FirstOrDefaultAsync(m => m.Id == milestone.Id, ct)
            ?? throw new InvalidOperationException("Milestone not found.");

        ValidateMilestone(milestone);

        existing.Name = milestone.Name;
        existing.Description = milestone.Description;
        existing.DueDate = milestone.DueDate;
        existing.AchievedDate = milestone.AchievedDate;
        existing.Status = milestone.Status;
        existing.PaymentAmount = milestone.PaymentAmount;
        existing.SortOrder = milestone.SortOrder;
        existing.Notes = milestone.Notes;

        Reconcile(existing);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>An achieved milestone always carries the date it was achieved; a pending one never does.</summary>
    private static void Reconcile(ProjectMilestone milestone)
    {
        if (milestone.Status == MilestoneStatus.Achieved)
            milestone.AchievedDate ??= DateOnly.FromDateTime(DateTime.UtcNow);
        else if (milestone.Status == MilestoneStatus.Pending)
            milestone.AchievedDate = null;
    }

    private static void ValidateMilestone(ProjectMilestone milestone)
    {
        if (string.IsNullOrWhiteSpace(milestone.Name))
            throw new InvalidOperationException("Milestone name is required.");
        if (milestone.PaymentAmount is < 0)
            throw new InvalidOperationException("Payment amount cannot be negative.");
    }

    public async Task DeleteMilestoneAsync(int id, CancellationToken ct = default)
    {
        var milestone = await db.ProjectMilestones.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (milestone is null) return;
        db.ProjectMilestones.Remove(milestone);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ProjectTask>> ListOverdueTasksAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.ProjectTasks.Include(t => t.Project).AsNoTracking()
            .Where(t => OpenTaskStatuses.Contains(t.Status)
                        && t.DueDate != null && t.DueDate < today
                        && OpenStatuses.Contains(t.Project.Status))
            .OrderBy(t => t.DueDate)
            .ToListAsync(ct);
    }

    public async Task<List<ProjectTask>> ListUpcomingTasksAsync(
        int withinDays = 14, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(withinDays);
        return await db.ProjectTasks.Include(t => t.Project).AsNoTracking()
            .Where(t => OpenTaskStatuses.Contains(t.Status)
                        && t.DueDate != null && t.DueDate >= today && t.DueDate <= cutoff
                        && OpenStatuses.Contains(t.Project.Status))
            .OrderBy(t => t.DueDate)
            .ToListAsync(ct);
    }

    public async Task<List<ProjectMilestone>> ListUpcomingMilestonesAsync(
        int withinDays = 30, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(withinDays);
        return await db.ProjectMilestones.Include(m => m.Project).AsNoTracking()
            .Where(m => m.Status == MilestoneStatus.Pending && m.DueDate <= cutoff
                        && OpenStatuses.Contains(m.Project.Status))
            .OrderBy(m => m.DueDate)
            .ToListAsync(ct);
    }

    public async Task<List<ProjectTask>> ListTasksForUserAsync(string userId, CancellationToken ct = default) =>
        await db.ProjectTasks.Include(t => t.Project).AsNoTracking()
            .Where(t => t.AssignedToUserId == userId
                        && OpenTaskStatuses.Contains(t.Status)
                        && OpenStatuses.Contains(t.Project.Status))
            .OrderBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ToListAsync(ct);
}
