using ErpPlatform.TestSupport;
using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;
using Tender.Domain;
using Tender.Infrastructure;
using Xunit;

namespace Tender.Tests;

/// <summary>
/// The physical file register. The thing being protected is the movement chain:
/// a file's status must never move without leaving a dated row saying who had it,
/// because "who took this last" is the only question the register exists to answer.
/// </summary>
public class FileRegistryTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_tender_test_{Guid.NewGuid():N}"[..30];
    private bool _available;

    /// <summary>
    /// Frozen at midday UTC on a fixed date, in the business timezone. Date-boundary
    /// assertions are then a fixture rather than a race against when the suite runs —
    /// which is the whole reason the clock is injected.
    /// </summary>
    private static readonly IBusinessClock Clock =
        new FixedClock(new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);

    private static DateOnly Today => Clock.Today;

    private DbContextOptions<TenderDbContext> Opts() =>
        new DbContextOptionsBuilder<TenderDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

    private TenderDbContext NewDb() => new(Opts(), new TestUser());

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { await db.Database.EnsureCreatedAsync(); _available = true; }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;   // nothing was created, so nothing to drop
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
    }

    private static ProjectService Projects(TenderDbContext db) =>
        new(db, new FileRegistryService(db, Clock), Clock);

    private static TenderService Tenders(TenderDbContext db) =>
        new(db, new FileRegistryService(db, Clock), Clock);

    [SkippableFact]
    public async Task Creating_a_project_opens_its_file_with_a_number()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var project = await Projects(db).CreateAsync(new Project
        { ProjectCode = "PRJ-001", Name = "Head Office Rewiring" });

        var file = await new FileRegistryService(db, Clock)
            .FindForOwnerAsync(FileOwnerType.Project, project.Id);

        Assert.NotNull(file);
        Assert.StartsWith("FILE-", file!.FileNumber);
        Assert.Equal("PRJ-001", file.OwnerReference);
        Assert.Equal(FileStatus.InRegistry, file.Status);
        // Opening the file is itself a movement, so the chain starts complete.
        Assert.Single(file.Movements);
        Assert.Equal(FileMovementAction.Opened, file.Movements[0].Action);
    }

    [SkippableFact]
    public async Task Creating_a_tender_opens_its_file_too_and_numbers_run_on()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        await Tenders(db).CreateAsync(new TenderRecord
        { TenderNumber = "T-001", Title = "Supply of pumps", IssuingAuthority = "KWSB" });
        await Projects(db).CreateAsync(new Project { ProjectCode = "PRJ-001", Name = "Rewiring" });

        var files = await new FileRegistryService(db, Clock).ListAsync();

        Assert.Equal(2, files.Count);
        // One sequence across both registers — a file number is unique on its own.
        Assert.Equal(2, files.Select(f => f.FileNumber).Distinct().Count());
        Assert.Contains(files, f => f.OwnerType == FileOwnerType.Tender);
        Assert.Contains(files, f => f.OwnerType == FileOwnerType.Project);
    }

    [SkippableFact]
    public async Task A_second_call_returns_the_same_file_rather_than_a_new_one()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var files = new FileRegistryService(db, Clock);

        var first = await files.EnsureForAsync(FileOwnerType.Project, 42, "PRJ-042", "Something");
        var second = await files.EnsureForAsync(FileOwnerType.Project, 42, "PRJ-042", "Something");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.FileNumber, second.FileNumber);
    }

    [SkippableFact]
    public async Task Issuing_then_returning_writes_both_movements_and_ends_in_the_registry()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = new FileRegistryService(db, Clock);
        var file = await svc.EnsureForAsync(FileOwnerType.Project, 1, "PRJ-001", "Rewiring");

        await svc.IssueAsync(file.Id, "u1", "Ali", "Site visit",
            Today.AddDays(7), "u9", "Clerk");

        var issued = await svc.GetAsync(file.Id);
        Assert.Equal(FileStatus.Issued, issued!.Status);
        Assert.Equal("Ali", issued.HolderName);

        await svc.ReturnAsync(file.Id, "Cabinet 3", "u9", "Clerk");

        var returned = await svc.GetAsync(file.Id);
        Assert.Equal(FileStatus.InRegistry, returned!.Status);
        Assert.Null(returned.HolderName);
        Assert.Equal("Cabinet 3", returned.Location);
        // Opened + Issued + Returned.
        Assert.Equal(3, returned.Movements.Count);
        Assert.Contains(returned.Movements, m => m.Action == FileMovementAction.Issued
                                                 && m.ToHolderName == "Ali");
    }

    [SkippableFact]
    public async Task A_file_that_is_already_out_cannot_be_issued_again()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = new FileRegistryService(db, Clock);
        var file = await svc.EnsureForAsync(FileOwnerType.Project, 1, "PRJ-001", "Rewiring");

        await svc.IssueAsync(file.Id, "u1", "Ali", null, null, "u9", "Clerk");

        // Two holders at once is precisely how a file goes missing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.IssueAsync(file.Id, "u2", "Bilal", null, null, "u9", "Clerk"));
    }

    [SkippableFact]
    public async Task Handing_on_keeps_it_out_and_records_who_it_came_from()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = new FileRegistryService(db, Clock);
        var file = await svc.EnsureForAsync(FileOwnerType.Project, 1, "PRJ-001", "Rewiring");

        await svc.IssueAsync(file.Id, "u1", "Ali", null, null, "u9", "Clerk");
        await svc.TransferAsync(file.Id, "u2", "Bilal", "Audit", null, "u9", "Clerk");

        var after = await svc.GetAsync(file.Id);
        Assert.Equal(FileStatus.Issued, after!.Status);
        Assert.Equal("Bilal", after.HolderName);

        var transfer = after.Movements.First(m => m.Action == FileMovementAction.Transferred);
        Assert.Equal("Ali", transfer.FromHolderName);
        Assert.Equal("Bilal", transfer.ToHolderName);
    }

    [SkippableFact]
    public async Task A_file_still_out_cannot_be_archived()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = new FileRegistryService(db, Clock);
        var file = await svc.EnsureForAsync(FileOwnerType.Project, 1, "PRJ-001", "Rewiring");

        await svc.IssueAsync(file.Id, "u1", "Ali", null, null, "u9", "Clerk");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ArchiveAsync(file.Id, "Basement", "u9", "Clerk"));
    }

    [SkippableFact]
    public async Task Overdue_counts_only_files_out_past_their_due_date()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = new FileRegistryService(db, Clock);
        var yesterday = Today.AddDays(-1);
        var nextWeek = Today.AddDays(7);

        var late = await svc.EnsureForAsync(FileOwnerType.Project, 1, "PRJ-001", "Late one");
        var fine = await svc.EnsureForAsync(FileOwnerType.Project, 2, "PRJ-002", "Fine one");
        var undated = await svc.EnsureForAsync(FileOwnerType.Project, 3, "PRJ-003", "No date");

        await svc.IssueAsync(late.Id, "u1", "Ali", null, yesterday, "u9", "Clerk");
        await svc.IssueAsync(fine.Id, "u2", "Bilal", null, nextWeek, "u9", "Clerk");
        await svc.IssueAsync(undated.Id, "u3", "Chaudhry", null, null, "u9", "Clerk");

        var overdue = await svc.ListOverdueAsync();

        Assert.Single(overdue);
        Assert.Equal("PRJ-001", overdue[0].OwnerReference);
    }

    [SkippableFact]
    public async Task A_lost_file_must_be_found_before_it_can_be_issued()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = new FileRegistryService(db, Clock);
        var file = await svc.EnsureForAsync(FileOwnerType.Project, 1, "PRJ-001", "Rewiring");

        await svc.MarkLostAsync(file.Id, "u9", "Clerk", "Not on the shelf.");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.IssueAsync(file.Id, "u1", "Ali", null, null, "u9", "Clerk"));

        await svc.MarkFoundAsync(file.Id, "Cabinet 3", "u9", "Clerk");
        var found = await svc.GetAsync(file.Id);
        Assert.Equal(FileStatus.InRegistry, found!.Status);

        // Now it issues without complaint.
        await svc.IssueAsync(file.Id, "u1", "Ali", null, null, "u9", "Clerk");
        Assert.Equal(FileStatus.Issued, (await svc.GetAsync(file.Id))!.Status);
    }

    [SkippableFact]
    public async Task A_scanned_number_resolves_back_to_its_file()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = new FileRegistryService(db, Clock);
        var file = await svc.EnsureForAsync(FileOwnerType.Tender, 7, "T-007", "Supply of pumps");

        // Scanners often deliver trailing whitespace with the payload.
        var found = await svc.GetByNumberAsync($"  {file.FileNumber} ");

        Assert.NotNull(found);
        Assert.Equal(file.Id, found!.Id);
    }

    [SkippableFact]
    public async Task Renaming_the_owner_updates_the_registry_snapshot()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var projects = Projects(db);
        var project = await projects.CreateAsync(new Project
        { ProjectCode = "PRJ-001", Name = "Original name" });

        project.Name = "Renamed";
        await projects.UpdateAsync(project);

        var file = await new FileRegistryService(db, Clock)
            .FindForOwnerAsync(FileOwnerType.Project, project.Id);

        Assert.Equal("Renamed", file!.OwnerTitle);
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
