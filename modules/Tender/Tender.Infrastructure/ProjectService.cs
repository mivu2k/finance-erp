using ErpPlatform.Shared.Kernel;
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

    Task<ProjectMilestone> AddMilestoneAsync(int projectId, ProjectMilestone milestone, CancellationToken ct = default);
    Task<ProjectMilestone> UpdateMilestoneAsync(ProjectMilestone milestone, CancellationToken ct = default);
    Task DeleteMilestoneAsync(int id, CancellationToken ct = default);

    /// <summary>Pending milestones due within the window.</summary>
    Task<List<ProjectMilestone>> ListUpcomingMilestonesAsync(int withinDays = 30, CancellationToken ct = default);
}

public class ProjectService(TenderDbContext db, IFileRegistryService files, IBusinessClock clock) : IProjectService
{
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

        db.WorkTasks.RemoveRange(project.Tasks);
        db.ProjectMilestones.RemoveRange(project.Milestones);
        db.Projects.Remove(project);
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

        Reconcile(milestone, clock.Today);
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

        Reconcile(existing, clock.Today);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>An achieved milestone always carries the date it was achieved; a pending one never does.</summary>
    private static void Reconcile(ProjectMilestone milestone, DateOnly today)
    {
        if (milestone.Status == MilestoneStatus.Achieved)
            milestone.AchievedDate ??= today;
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

    public async Task<List<ProjectMilestone>> ListUpcomingMilestonesAsync(
        int withinDays = 30, CancellationToken ct = default)
    {
        var cutoff = clock.Today.AddDays(withinDays);
        return await db.ProjectMilestones.Include(m => m.Project).AsNoTracking()
            .Where(m => m.Status == MilestoneStatus.Pending && m.DueDate <= cutoff
                        && (m.Project.Status == ProjectStatus.Planned
                            || m.Project.Status == ProjectStatus.Active
                            || m.Project.Status == ProjectStatus.OnHold))
            .OrderBy(m => m.DueDate)
            .ToListAsync(ct);
    }
}
