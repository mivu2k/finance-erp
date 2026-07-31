using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;
using Tender.Domain;
using Tender.Infrastructure;
using Xunit;

namespace Tender.Tests;

/// <summary>
/// Projects, their tasks and their milestones. The rules worth pinning down are the
/// ones where three fields all claim to describe the same thing: a task's status, its
/// percentage and its completion date must never disagree, and a project's own
/// progress is read from its tasks rather than stored beside them.
/// </summary>
public class ProjectTaskTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_tender_test_{Guid.NewGuid():N}"[..30];
    private bool _available;

    private DbContextOptions<TenderDbContext> Opts() =>
        new DbContextOptionsBuilder<TenderDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

    private TenderDbContext NewDb() => new(Opts(), new TestUser());

    /// <summary>Creating a project opens its physical file, so the registry comes along.</summary>
    private static ProjectService Projects(TenderDbContext db) => new(db, new FileRegistryService(db));

    private static WorkTaskService Tasks(TenderDbContext db) => new(db);

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { await db.Database.EnsureCreatedAsync(); _available = true; }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<int> SeedProjectAsync()
    {
        await using var db = NewDb();
        var project = await Projects(db).CreateAsync(new Project
        {
            ProjectCode = "PRJ-001",
            Name = "Head Office Rewiring",
            Status = ProjectStatus.Active
        });
        return project.Id;
    }

    [Fact]
    public async Task Progress_averages_the_tasks_and_ignores_cancelled_ones()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();

        await using (var db = NewDb())
        {
            var svc = Tasks(db);
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask
            { Title = "Survey", Status = ProjectTaskStatus.Completed });
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask
            { Title = "Cabling", Status = ProjectTaskStatus.InProgress, ProgressPercent = 40 });
            // Abandoned work must not flatter the figure by counting as delivered.
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask
            { Title = "Dropped scope", Status = ProjectTaskStatus.Cancelled });
        }

        await using (var db = NewDb())
        {
            var project = await Projects(db).GetAsync(projectId);
            Assert.NotNull(project);
            Assert.Equal(3, project!.Tasks.Count);
            Assert.Equal(70, project.ProgressPercent); // (100 + 40) / 2
        }
    }

    [Fact]
    public async Task Completing_a_task_fills_in_its_progress_and_completion_date()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();

        int taskId;
        await using (var db = NewDb())
        {
            var task = await Tasks(db)
                .AddAsync(WorkOwnerType.Project, projectId, new WorkTask { Title = "Survey", ProgressPercent = 10 });
            taskId = task.Id;
        }

        await using (var db = NewDb())
            await Tasks(db).SetStatusAsync(taskId, ProjectTaskStatus.Completed);

        await using (var db = NewDb())
        {
            var task = await db.WorkTasks.FirstAsync(t => t.Id == taskId);
            Assert.Equal(ProjectTaskStatus.Completed, task.Status);
            Assert.Equal(100, task.ProgressPercent);
            Assert.NotNull(task.CompletedDate);
        }
    }

    [Fact]
    public async Task Reopening_a_completed_task_clears_the_completion_date()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();

        int taskId;
        await using (var db = NewDb())
        {
            var task = await Tasks(db).AddAsync(WorkOwnerType.Project, projectId,
                new WorkTask { Title = "Cabling", Status = ProjectTaskStatus.Completed });
            taskId = task.Id;
        }

        await using (var db = NewDb())
            await Tasks(db).SetStatusAsync(taskId, ProjectTaskStatus.InProgress);

        await using (var db = NewDb())
        {
            var task = await db.WorkTasks.FirstAsync(t => t.Id == taskId);
            // A finished-then-reopened task that kept its date would still read as delivered.
            Assert.Null(task.CompletedDate);
            Assert.True(task.ProgressPercent < 100);
        }
    }

    [Fact]
    public async Task A_task_with_progress_cannot_stay_NotStarted()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();

        await using var db = NewDb();
        var task = await Tasks(db).AddAsync(WorkOwnerType.Project, projectId, new WorkTask
        { Title = "Panel install", Status = ProjectTaskStatus.NotStarted, ProgressPercent = 25 });

        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);
    }

    [Fact]
    public async Task Overdue_lists_only_open_tasks_past_their_date()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        await using (var db = NewDb())
        {
            var svc = Tasks(db);
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask
            { Title = "Late", DueDate = yesterday });
            // Same date, but finished — not chased.
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask
            { Title = "Done on time", DueDate = yesterday, Status = ProjectTaskStatus.Completed });
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask
            { Title = "Future", DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });
        }

        await using (var db = NewDb())
        {
            var overdue = await Tasks(db).ListOverdueAsync();
            Assert.Single(overdue);
            Assert.Equal("Late", overdue[0].Title);
        }
    }

    [Fact]
    public async Task A_duplicate_project_code_is_refused()
    {
        if (!_available) return;
        await SeedProjectAsync();

        await using var db = NewDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Projects(db).CreateAsync(new Project { ProjectCode = "PRJ-001", Name = "Another" }));
    }

    [Fact]
    public async Task Achieving_a_milestone_stamps_the_date_and_pending_clears_it()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();

        int milestoneId;
        await using (var db = NewDb())
        {
            var milestone = await Projects(db).AddMilestoneAsync(projectId,
                new ProjectMilestone
                {
                    Name = "First handover",
                    DueDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Status = MilestoneStatus.Achieved
                });
            milestoneId = milestone.Id;
            Assert.NotNull(milestone.AchievedDate);
        }

        await using (var db = NewDb())
        {
            var svc = Projects(db);
            var milestone = await db.ProjectMilestones.FirstAsync(m => m.Id == milestoneId);
            milestone.Status = MilestoneStatus.Pending;
            var updated = await svc.UpdateMilestoneAsync(milestone);
            Assert.Null(updated.AchievedDate);
        }
    }

    [Fact]
    public async Task Deleting_a_project_takes_its_tasks_and_milestones_with_it()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();

        await using (var db = NewDb())
        {
            await Tasks(db).AddAsync(WorkOwnerType.Project, projectId, new WorkTask { Title = "Survey" });
            await Projects(db).AddMilestoneAsync(projectId, new ProjectMilestone
            { Name = "Handover", DueDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        }

        await using (var db = NewDb())
            await Projects(db).DeleteAsync(projectId);

        await using (var db = NewDb())
        {
            // Soft delete: the rows survive, the query filter hides them.
            Assert.Null(await Projects(db).GetAsync(projectId));
            Assert.Empty(await db.WorkTasks.ToListAsync());
            Assert.Empty(await db.ProjectMilestones.ToListAsync());
        }
    }

    [Fact]
    public async Task Tasks_are_numbered_in_the_order_they_are_added()
    {
        if (!_available) return;
        var projectId = await SeedProjectAsync();

        await using var db = NewDb();
        var svc = Tasks(db);
        var first = await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask { Title = "One" });
        var second = await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask { Title = "Two" });

        Assert.Equal(1, first.SortOrder);
        Assert.Equal(2, second.SortOrder);
    }

    private sealed class TestUser : ICurrentUserService
    {
        public string? UserId => "test";
        public string? UserName => "test";
        public string? IpAddress => null;
        public string? Browser => null;
        public bool HasPermission(string permission) => true;
    }
}
