using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;
using Tender.Domain;
using Tender.Infrastructure;
using Xunit;

namespace Tender.Tests;

/// <summary>
/// A tender's schedule of items and its bid checklist. Two things are being pinned
/// down: a tender is a list of priced lines rather than one figure (and equally may be
/// no lines at all), and tasks belong to tenders as much as to projects.
/// </summary>
public class TenderItemAndTaskTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_tender_test_{Guid.NewGuid():N}"[..30];
    private bool _available;

    private DbContextOptions<TenderDbContext> Opts() =>
        new DbContextOptionsBuilder<TenderDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

    private TenderDbContext NewDb() => new(Opts(), new TestUser());

    private static TenderService Tenders(TenderDbContext db) => new(db, new FileRegistryService(db));
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

    private async Task<int> SeedTenderAsync(decimal estimate = 0)
    {
        await using var db = NewDb();
        var tender = await Tenders(db).CreateAsync(new TenderRecord
        {
            TenderNumber = "T-001",
            Title = "Supply of laboratory equipment",
            IssuingAuthority = "KWSB",
            EstimatedValue = estimate,
            Status = TenderStatus.InPreparation
        });
        return tender.Id;
    }

    [Fact]
    public async Task A_tender_carries_many_item_lines_and_totals_them()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        await using (var db = NewDb())
        {
            var svc = Tenders(db);
            await svc.AddItemAsync(tenderId, new TenderItem
            { Description = "Centrifuge", Unit = "Nos", Quantity = 2, UnitRate = 150_000 });
            await svc.AddItemAsync(tenderId, new TenderItem
            { Description = "Microscope", Unit = "Nos", Quantity = 5, UnitRate = 80_000 });
            await svc.AddItemAsync(tenderId, new TenderItem
            { Description = "Installation", Unit = "Lump sum", Quantity = 1, UnitRate = 40_000 });
        }

        await using (var db = NewDb())
        {
            var tender = await Tenders(db).GetAsync(tenderId);
            Assert.Equal(3, tender!.Items.Count);
            Assert.True(tender.HasSchedule);
            // 300,000 + 400,000 + 40,000
            Assert.Equal(740_000m, tender.ItemsTotal);
        }
    }

    [Fact]
    public async Task A_tender_with_no_schedule_is_perfectly_valid()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync(estimate: 500_000);

        await using var db = NewDb();
        var tender = await Tenders(db).GetAsync(tenderId);

        // A lump-sum bid carries no lines rather than one dummy line for the whole thing.
        Assert.Empty(tender!.Items);
        Assert.False(tender.HasSchedule);
        Assert.Equal(0m, tender.ItemsTotal);
        Assert.Equal(500_000m, tender.EstimatedValue);
    }

    [Fact]
    public async Task The_schedule_total_is_kept_apart_from_the_estimate()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync(estimate: 500_000);

        await using (var db = NewDb())
            await Tenders(db).AddItemAsync(tenderId, new TenderItem
            { Description = "Centrifuge", Quantity = 2, UnitRate = 150_000 });

        await using (var db = NewDb())
        {
            var tender = await Tenders(db).GetAsync(tenderId);
            // Pricing the schedule must not silently overwrite the estimate — seeing the
            // two disagree is how a mispriced line gets caught before submission.
            Assert.Equal(500_000m, tender!.EstimatedValue);
            Assert.Equal(300_000m, tender.ItemsTotal);
        }
    }

    [Fact]
    public async Task A_line_reports_its_margin_only_when_a_cost_is_known()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        await using var db = NewDb();
        var svc = Tenders(db);

        var priced = await svc.AddItemAsync(tenderId, new TenderItem
        { Description = "Centrifuge", Quantity = 2, UnitRate = 150_000, CostRate = 100_000 });
        var unpriced = await svc.AddItemAsync(tenderId, new TenderItem
        { Description = "Microscope", Quantity = 5, UnitRate = 80_000 });

        Assert.Equal(300_000m, priced.Amount);
        Assert.Equal(200_000m, priced.CostAmount);
        Assert.Equal(100_000m, priced.Margin);
        Assert.Equal(33.33m, priced.MarginPercent);

        // Null, not zero: an unpriced line has no margin, not a nil one.
        Assert.Null(unpriced.CostAmount);
        Assert.Null(unpriced.Margin);
        Assert.Null(unpriced.MarginPercent);
    }

    [Fact]
    public async Task Items_are_numbered_in_the_order_they_are_added()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        await using var db = NewDb();
        var svc = Tenders(db);
        var first = await svc.AddItemAsync(tenderId, new TenderItem { Description = "One" });
        var second = await svc.AddItemAsync(tenderId, new TenderItem { Description = "Two" });

        Assert.Equal(1, first.SortOrder);
        Assert.Equal(2, second.SortOrder);
    }

    [Fact]
    public async Task An_item_needs_a_description_and_non_negative_figures()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        await using var db = NewDb();
        var svc = Tenders(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddItemAsync(tenderId, new TenderItem { Description = "  " }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddItemAsync(tenderId, new TenderItem { Description = "X", Quantity = -1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddItemAsync(tenderId, new TenderItem { Description = "X", UnitRate = -1 }));
    }

    [Fact]
    public async Task A_tender_carries_its_own_tasks()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        await using (var db = NewDb())
        {
            var svc = Tasks(db);
            await svc.AddAsync(WorkOwnerType.Tender, tenderId, new WorkTask
            { Title = "Collect tax certificate" });
            await svc.AddAsync(WorkOwnerType.Tender, tenderId, new WorkTask
            { Title = "Arrange bank guarantee", Status = ProjectTaskStatus.InProgress, ProgressPercent = 50 });
        }

        await using (var db = NewDb())
        {
            var tender = await Tenders(db).GetAsync(tenderId);
            Assert.Equal(2, tender!.Tasks.Count);
            Assert.All(tender.Tasks, t => Assert.Equal(tenderId, t.TenderRecordId));
            Assert.All(tender.Tasks, t => Assert.Null(t.ProjectId));
        }
    }

    [Fact]
    public async Task Tender_and_project_tasks_stay_on_their_own_boards()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        int projectId;
        await using (var db = NewDb())
            projectId = (await Projects(db).CreateAsync(new Project
            { ProjectCode = "PRJ-001", Name = "Rewiring" })).Id;

        await using (var db = NewDb())
        {
            var svc = Tasks(db);
            await svc.AddAsync(WorkOwnerType.Tender, tenderId, new WorkTask { Title = "Bid task" });
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask { Title = "Site task" });
        }

        await using (var db = NewDb())
        {
            var svc = Tasks(db);
            var tenderTasks = await svc.ListForAsync(WorkOwnerType.Tender, tenderId);
            var projectTasks = await svc.ListForAsync(WorkOwnerType.Project, projectId);

            Assert.Equal("Bid task", Assert.Single(tenderTasks).Title);
            Assert.Equal("Site task", Assert.Single(projectTasks).Title);

            // Ordering restarts per owner rather than running on across both.
            Assert.Equal(1, tenderTasks[0].SortOrder);
            Assert.Equal(1, projectTasks[0].SortOrder);
        }
    }

    [Fact]
    public async Task The_same_reconcile_rules_apply_to_a_tender_task()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        await using var db = NewDb();
        var svc = Tasks(db);

        var task = await svc.AddAsync(WorkOwnerType.Tender, tenderId, new WorkTask
        { Title = "Sign technical bid", Status = ProjectTaskStatus.NotStarted, ProgressPercent = 20 });
        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);

        var done = await svc.SetStatusAsync(task.Id, ProjectTaskStatus.Completed);
        Assert.Equal(100, done.ProgressPercent);
        Assert.NotNull(done.CompletedDate);
    }

    [Fact]
    public async Task Overdue_spans_both_registers_but_skips_a_lost_tender()
    {
        if (!_available) return;
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        var liveId = await SeedTenderAsync();
        int lostId, projectId;

        await using (var db = NewDb())
        {
            lostId = (await Tenders(db).CreateAsync(new TenderRecord
            {
                TenderNumber = "T-002", Title = "Lost one", IssuingAuthority = "KWSB",
                Status = TenderStatus.Lost
            })).Id;

            projectId = (await Projects(db).CreateAsync(new Project
            { ProjectCode = "PRJ-001", Name = "Rewiring", Status = ProjectStatus.Active })).Id;
        }

        await using (var db = NewDb())
        {
            var svc = Tasks(db);
            await svc.AddAsync(WorkOwnerType.Tender, liveId, new WorkTask
            { Title = "Live tender task", DueDate = yesterday });
            await svc.AddAsync(WorkOwnerType.Tender, lostId, new WorkTask
            { Title = "Lost tender task", DueDate = yesterday });
            await svc.AddAsync(WorkOwnerType.Project, projectId, new WorkTask
            { Title = "Project task", DueDate = yesterday });
        }

        await using (var db = NewDb())
        {
            var overdue = await Tasks(db).ListOverdueAsync();
            var titles = overdue.Select(t => t.Title).ToList();

            Assert.Contains("Live tender task", titles);
            Assert.Contains("Project task", titles);
            // Nobody is chasing a checklist on a bid that was lost.
            Assert.DoesNotContain("Lost tender task", titles);
        }
    }

    [Fact]
    public async Task Deleting_a_tender_takes_its_schedule_and_tasks_with_it()
    {
        if (!_available) return;
        var tenderId = await SeedTenderAsync();

        await using (var db = NewDb())
        {
            await Tenders(db).AddItemAsync(tenderId, new TenderItem { Description = "Centrifuge" });
            await Tasks(db).AddAsync(WorkOwnerType.Tender, tenderId, new WorkTask { Title = "Checklist" });
        }

        await using (var db = NewDb())
            await Tenders(db).DeleteAsync(tenderId);

        await using (var db = NewDb())
        {
            // Soft delete: the rows survive, the query filters hide them.
            Assert.Null(await Tenders(db).GetAsync(tenderId));
            Assert.Empty(await db.TenderItems.ToListAsync());
            Assert.Empty(await db.WorkTasks.ToListAsync());
        }
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
