using ErpPlatform.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

/// <summary>One device on an intake form, before it becomes a job.</summary>
public record IntakeDeviceDto(
    string DeviceName,
    string Brand,
    string? Model,
    string? SerialNumber,
    DeviceCondition Condition,
    string IssueDescription,
    JobPriority Priority,
    DateOnly? ExpectedDelivery,
    IReadOnlyList<int> SymptomIds,
    IReadOnlyList<int> AccessoryIds);

public interface IIntakeService
{
    Task<List<Intake>> ListAsync(string? search = null, CancellationToken ct = default);
    Task<Intake?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Books a drop-off in. Creates the intake and one repair job per device,
    /// numbering both, in a single transaction.
    /// </summary>
    Task<Intake> ReceiveAsync(Intake intake, IReadOnlyList<IntakeDeviceDto> devices,
        string receivedById, string receivedByName, CancellationToken ct = default);
}

public class IntakeService(RepairDbContext db) : IIntakeService
{
    public async Task<List<Intake>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        var q = db.Intakes.Include(i => i.Customer).Include(i => i.Jobs)
            .AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(i => i.IntakeNumber.Contains(s)
                          || i.Customer.Name.Contains(s)
                          || i.Customer.Phone.Contains(s));
        }

        return await q.OrderByDescending(i => i.Id).Take(300).ToListAsync(ct);
    }

    public Task<Intake?> GetAsync(int id, CancellationToken ct = default) =>
        db.Intakes
            .Include(i => i.Customer)
            .Include(i => i.Jobs)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<Intake> ReceiveAsync(
        Intake intake, IReadOnlyList<IntakeDeviceDto> devices,
        string receivedById, string receivedByName, CancellationToken ct = default)
    {
        if (intake.CustomerId == 0)
            throw new InvalidOperationException("Select a customer.");
        if (devices.Count == 0)
            throw new InvalidOperationException("Add at least one device.");
        if (devices.Any(d => string.IsNullOrWhiteSpace(d.DeviceName)))
            throw new InvalidOperationException("Every device needs a name.");
        if (devices.Any(d => string.IsNullOrWhiteSpace(d.IssueDescription)))
            throw new InvalidOperationException("Every device needs a reported fault.");

        // EnableRetryOnFailure requires transactions to run through the execution
        // strategy so a retried attempt re-runs the whole unit, not a half-open one.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var numbers = new DocumentNumberService(db);
            var now = DateTime.UtcNow;

            intake.IntakeNumber = await numbers.NextAsync("Intake", "INT", ct);
            intake.ReceivedAtUtc = now;
            intake.ReceivedById = receivedById;
            intake.ReceivedByName = receivedByName;
            db.Intakes.Add(intake);

            foreach (var device in devices)
            {
                var job = new RepairJob
                {
                    JobNumber = await numbers.NextAsync("RepairJob", "JOB", ct),
                    Intake = intake,
                    CustomerId = intake.CustomerId,
                    DeviceName = device.DeviceName.Trim(),
                    Brand = device.Brand,
                    Model = device.Model,
                    SerialNumber = device.SerialNumber,
                    ConditionOnArrival = device.Condition,
                    IssueDescription = device.IssueDescription.Trim(),
                    Priority = device.Priority,
                    ExpectedDeliveryDate = device.ExpectedDelivery,
                    Status = JobStatus.Received,
                    StatusUpdatedAtUtc = now,
                    Symptoms = device.SymptomIds.Distinct()
                        .Select(id => new JobSymptom { SymptomId = id }).ToList(),
                    Accessories = device.AccessoryIds.Distinct()
                        .Select(id => new JobAccessory { AccessoryId = id }).ToList()
                };

                job.StatusHistory.Add(new JobStatusHistory
                {
                    ChangedById = receivedById,
                    ChangedByName = receivedByName,
                    FromStatus = JobStatus.Received,
                    ToStatus = JobStatus.Received,
                    Note = "Received at the counter"
                });

                intake.Jobs.Add(job);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return intake;
        });
    }
}
