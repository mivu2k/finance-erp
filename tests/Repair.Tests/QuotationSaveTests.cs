using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;
using Repair.Domain;
using Repair.Infrastructure;
using Xunit;

namespace Repair.Tests;

/// <summary>
/// A collective quotation carries a Customer/RepairJob/Intake navigation copied
/// from a loaded, graph-connected source (BuildForIntakeAsync/BuildForJobAsync).
/// If two jobs on the same intake share a part, that graph still reaches
/// Jobs -> WorkItems -> Part through Customer.Intakes' back-reference, and Add()
/// cascades into two untracked Part instances for the same key.
/// </summary>
public class QuotationSaveTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_repair_test_{Guid.NewGuid():N}"[..28];
    private bool _available;

    private DbContextOptions<RepairDbContext> Opts() =>
        new DbContextOptionsBuilder<RepairDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

    public async Task InitializeAsync()
    {
        await using var db = new RepairDbContext(Opts(), new TestUser());
        try { await db.Database.EnsureCreatedAsync(); _available = true; }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        await using var db = new RepairDbContext(Opts(), new TestUser());
        await db.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Combined_quotation_saves_when_two_jobs_share_a_part()
    {
        if (!_available) return;

        int intakeId;
        await using (var seedDb = new RepairDbContext(Opts(), new TestUser()))
        {
            var customer = new Customer { Name = "ABC", Phone = "0321-1234567" };
            var part = new Part { Sku = "MB-1", Name = "Motherboard", Price = 600 };
            seedDb.Customers.Add(customer);
            seedDb.Parts.Add(part);
            await seedDb.SaveChangesAsync();

            var intake = new Intake
            {
                IntakeNumber = "INT-26-0001",
                CustomerId = customer.Id,
                ReceivedById = "u1",
                ReceivedByName = "Tester",
                ReceivedAtUtc = DateTime.UtcNow
            };
            seedDb.Intakes.Add(intake);
            await seedDb.SaveChangesAsync();
            intakeId = intake.Id;

            for (var n = 1; n <= 2; n++)
            {
                var job = new RepairJob
                {
                    JobNumber = $"JOB-26-000{n}",
                    IntakeId = intake.Id,
                    CustomerId = customer.Id,
                    DeviceName = n == 1 ? "Micro Compressor NX-1200" : "Micro Generator NX-1700",
                    Brand = "Mothe",
                    IssueDescription = "Not powering on",
                    Status = JobStatus.Received,
                    StatusUpdatedAtUtc = DateTime.UtcNow
                };
                job.WorkItems.Add(new JobWorkItem
                {
                    Kind = JobWorkItemKind.Part,
                    PartId = part.Id,
                    Description = "Motherboard",
                    Quantity = 1,
                    UnitPrice = 600,
                    LineTotal = 600,
                    Billable = true
                });
                seedDb.RepairJobs.Add(job);
            }
            await seedDb.SaveChangesAsync();
        }

        Quotation built;
        await using (var buildDb = new RepairDbContext(Opts(), new TestUser()))
        {
            built = await new QuotationService(buildDb).BuildForIntakeAsync(intakeId);
        }

        Assert.Equal(2, built.Items.Count);

        await using var saveDb = new RepairDbContext(Opts(), new TestUser());
        var saved = await new QuotationService(saveDb).SaveAsync(built, "u1", "Tester");

        Assert.NotEqual(0, saved.Id);
        Assert.Equal(2, saved.Items.Count);
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
