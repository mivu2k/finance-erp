using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

public record JobFilter(
    string? Search = null,
    JobStatus? Status = null,
    bool OpenOnly = false,
    string? TechnicianId = null,
    JobPriority? Priority = null,
    bool OverdueOnly = false);

public interface IRepairJobService
{
    Task<List<RepairJob>> ListAsync(JobFilter filter, CancellationToken ct = default);
    Task<RepairJob?> GetAsync(int id, CancellationToken ct = default);
    Task<RepairJob?> GetByNumberAsync(string jobNumber, CancellationToken ct = default);
    Task<RepairJob> UpdateAsync(RepairJob job, CancellationToken ct = default);

    Task<RepairJob> AssignAsync(int id, string technicianId, string technicianName,
        string actorId, string actorName, CancellationToken ct = default);
    Task<RepairJob> ChangeStatusAsync(int id, JobStatus to, string? note,
        string actorId, string actorName, CancellationToken ct = default);

    /// <summary>
    /// Hands the device back, recording who collected it. Kept separate from a
    /// plain status change because the handover details are what the delivery note
    /// is signed against.
    /// </summary>
    Task<RepairJob> DeliverAsync(int id, string collectedByName, string? collectedByPhone,
        string? collectedByCnic, string? note, string actorId, string actorName,
        CancellationToken ct = default);

    Task<Diagnosis> AddDiagnosisAsync(Diagnosis diagnosis, CancellationToken ct = default);
    Task DeleteDiagnosisAsync(int diagnosisId, CancellationToken ct = default);

    Task SetSymptomsAsync(int jobId, IEnumerable<int> symptomIds, CancellationToken ct = default);

    /// <summary>
    /// Replaces the job's parts-and-labour list wholesale. The workshop edits it as
    /// one table, and a quotation built later reads exactly what is stored here.
    /// </summary>
    Task SetWorkItemsAsync(int jobId, IEnumerable<JobWorkItem> items, CancellationToken ct = default);
    Task SetAccessoriesAsync(int jobId, IEnumerable<int> accessoryIds, CancellationToken ct = default);
}

public class RepairJobService(RepairDbContext db) : IRepairJobService
{
    public async Task<List<RepairJob>> ListAsync(JobFilter filter, CancellationToken ct = default)
    {
        var q = db.RepairJobs.Include(j => j.Customer).AsNoTracking().AsQueryable();

        if (filter.Status is { } status) q = q.Where(j => j.Status == status);
        var open = JobWorkflow.Open;
        if (filter.OpenOnly) q = q.Where(j => open.Contains(j.Status));
        if (filter.Priority is { } priority) q = q.Where(j => j.Priority == priority);
        if (!string.IsNullOrWhiteSpace(filter.TechnicianId))
            q = q.Where(j => j.AssignedTechnicianId == filter.TechnicianId);

        if (filter.OverdueOnly)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            q = q.Where(j => j.ExpectedDeliveryDate != null
                          && j.ExpectedDeliveryDate < today
                          && open.Contains(j.Status));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(j => j.JobNumber.Contains(s)
                          || j.DeviceName.Contains(s)
                          || (j.SerialNumber != null && j.SerialNumber.Contains(s))
                          || j.Customer.Name.Contains(s)
                          || j.Customer.Phone.Contains(s));
        }

        return await q
            .OrderByDescending(j => j.Priority)
            .ThenByDescending(j => j.Id)
            .Take(500).ToListAsync(ct);
    }

    public Task<RepairJob?> GetAsync(int id, CancellationToken ct = default) =>
        Detailed().FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<RepairJob?> GetByNumberAsync(string jobNumber, CancellationToken ct = default) =>
        Detailed().FirstOrDefaultAsync(j => j.JobNumber == jobNumber, ct);

    private IQueryable<RepairJob> Detailed() =>
        db.RepairJobs
            .Include(j => j.Customer)
            .Include(j => j.Intake)
            .Include(j => j.Symptoms).ThenInclude(s => s.Symptom)
            .Include(j => j.Accessories).ThenInclude(a => a.Accessory)
            .Include(j => j.Diagnoses).ThenInclude(d => d.Part)
            .Include(j => j.Photos)
            .Include(j => j.WorkItems).ThenInclude(w => w.Part)
            .Include(j => j.StatusHistory);

    public async Task<RepairJob> UpdateAsync(RepairJob job, CancellationToken ct = default)
    {
        var existing = await db.RepairJobs.FirstOrDefaultAsync(j => j.Id == job.Id, ct)
                       ?? throw new InvalidOperationException("Job not found.");

        if (existing.Status is JobStatus.Delivered or JobStatus.Cancelled)
            throw new InvalidOperationException(
                $"A {JobWorkflow.Describe(existing.Status)} job can't be edited.");

        if (string.IsNullOrWhiteSpace(job.DeviceName))
            throw new InvalidOperationException("Device name is required.");
        if (string.IsNullOrWhiteSpace(job.IssueDescription))
            throw new InvalidOperationException("Reported fault is required.");

        existing.DeviceName = job.DeviceName;
        existing.Brand = job.Brand;
        existing.Model = job.Model;
        existing.SerialNumber = job.SerialNumber;
        existing.ConditionOnArrival = job.ConditionOnArrival;
        existing.IssueDescription = job.IssueDescription;
        existing.Priority = job.Priority;
        existing.ExpectedDeliveryDate = job.ExpectedDeliveryDate;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<RepairJob> AssignAsync(
        int id, string technicianId, string technicianName,
        string actorId, string actorName, CancellationToken ct = default)
    {
        var job = await db.RepairJobs.FirstOrDefaultAsync(j => j.Id == id, ct)
                  ?? throw new InvalidOperationException("Job not found.");

        if (job.Status is JobStatus.Delivered or JobStatus.Cancelled)
            throw new InvalidOperationException(
                $"A {JobWorkflow.Describe(job.Status)} job can't be reassigned.");

        job.AssignedTechnicianId = technicianId;
        job.AssignedTechnicianName = technicianName;

        // Picking up a freshly received job starts diagnosis — that's what
        // assignment means on the shop floor.
        if (job.Status == JobStatus.Received)
        {
            job.StatusHistory.Add(new JobStatusHistory
            {
                ChangedById = actorId,
                ChangedByName = actorName,
                FromStatus = job.Status,
                ToStatus = JobStatus.Diagnosing,
                Note = $"Assigned to {technicianName}"
            });
            job.Status = JobStatus.Diagnosing;
            job.StatusUpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            job.StatusHistory.Add(new JobStatusHistory
            {
                ChangedById = actorId,
                ChangedByName = actorName,
                FromStatus = job.Status,
                ToStatus = job.Status,
                Note = $"Assigned to {technicianName}"
            });
        }

        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<RepairJob> ChangeStatusAsync(
        int id, JobStatus to, string? note,
        string actorId, string actorName, CancellationToken ct = default)
    {
        var job = await db.RepairJobs.Include(j => j.StatusHistory)
                      .FirstOrDefaultAsync(j => j.Id == id, ct)
                  ?? throw new InvalidOperationException("Job not found.");

        JobWorkflow.EnsureCanMove(job.Status, to);

        job.StatusHistory.Add(new JobStatusHistory
        {
            ChangedById = actorId,
            ChangedByName = actorName,
            FromStatus = job.Status,
            ToStatus = to,
            Note = note
        });

        job.Status = to;
        job.StatusUpdatedAtUtc = DateTime.UtcNow;
        if (to == JobStatus.Delivered) job.DeliveredAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<RepairJob> DeliverAsync(
        int id, string collectedByName, string? collectedByPhone, string? collectedByCnic,
        string? note, string actorId, string actorName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collectedByName))
            throw new InvalidOperationException(
                "Record who collected the device — the delivery note is signed against it.");

        var job = await db.RepairJobs.Include(j => j.StatusHistory)
                      .FirstOrDefaultAsync(j => j.Id == id, ct)
                  ?? throw new InvalidOperationException("Job not found.");

        JobWorkflow.EnsureCanMove(job.Status, JobStatus.Delivered);

        job.StatusHistory.Add(new JobStatusHistory
        {
            ChangedById = actorId,
            ChangedByName = actorName,
            FromStatus = job.Status,
            ToStatus = JobStatus.Delivered,
            Note = $"Collected by {collectedByName}" +
                   (string.IsNullOrWhiteSpace(note) ? "" : $" — {note}")
        });

        job.Status = JobStatus.Delivered;
        job.StatusUpdatedAtUtc = DateTime.UtcNow;
        job.DeliveredAtUtc = DateTime.UtcNow;
        job.DeliveredToName = collectedByName.Trim();
        job.DeliveredToPhone = collectedByPhone;
        job.DeliveredToCnic = collectedByCnic;
        job.DeliveredByName = actorName;
        job.DeliveryNote = note;

        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<Diagnosis> AddDiagnosisAsync(Diagnosis diagnosis, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(diagnosis.Findings))
            throw new InvalidOperationException("Findings are required.");

        var job = await db.RepairJobs.FirstOrDefaultAsync(j => j.Id == diagnosis.RepairJobId, ct)
                  ?? throw new InvalidOperationException("Job not found.");
        if (job.Status is JobStatus.Delivered or JobStatus.Cancelled)
            throw new InvalidOperationException(
                $"A {JobWorkflow.Describe(job.Status)} job can't be diagnosed.");

        db.Diagnoses.Add(diagnosis);
        await db.SaveChangesAsync(ct);
        return diagnosis;
    }

    public async Task DeleteDiagnosisAsync(int diagnosisId, CancellationToken ct = default)
    {
        var diagnosis = await db.Diagnoses.FirstOrDefaultAsync(d => d.Id == diagnosisId, ct);
        if (diagnosis is null) return;
        db.Diagnoses.Remove(diagnosis);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetSymptomsAsync(
        int jobId, IEnumerable<int> symptomIds, CancellationToken ct = default)
    {
        var job = await db.RepairJobs.Include(j => j.Symptoms)
                      .FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw new InvalidOperationException("Job not found.");

        var wanted = symptomIds.Distinct().ToList();
        db.JobSymptoms.RemoveRange(job.Symptoms.Where(s => !wanted.Contains(s.SymptomId)));
        foreach (var id in wanted.Where(id => job.Symptoms.All(s => s.SymptomId != id)))
            job.Symptoms.Add(new JobSymptom { SymptomId = id });

        await db.SaveChangesAsync(ct);
    }

    public async Task SetWorkItemsAsync(
        int jobId, IEnumerable<JobWorkItem> items, CancellationToken ct = default)
    {
        var job = await db.RepairJobs.Include(j => j.WorkItems)
                      .FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw new InvalidOperationException("Job not found.");
        if (job.Status is JobStatus.Delivered or JobStatus.Cancelled)
            throw new InvalidOperationException("A delivered or cancelled job can't be re-costed.");

        var wanted = items.ToList();
        if (wanted.Any(i => string.IsNullOrWhiteSpace(i.Description)))
            throw new InvalidOperationException("Every parts/labour line needs a description.");
        if (wanted.Any(i => i.Quantity <= 0))
            throw new InvalidOperationException("Line quantities must be positive.");

        db.JobWorkItems.RemoveRange(job.WorkItems);
        job.WorkItems = wanted.Select(i => new JobWorkItem
        {
            Kind = i.Kind,
            PartId = i.PartId,
            Description = i.Description.Trim(),
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            LineTotal = Math.Round(i.Quantity * i.UnitPrice, 2),
            Billable = i.Billable,
            Notes = i.Notes
        }).ToList();

        await db.SaveChangesAsync(ct);
    }

    public async Task SetAccessoriesAsync(
        int jobId, IEnumerable<int> accessoryIds, CancellationToken ct = default)
    {
        var job = await db.RepairJobs.Include(j => j.Accessories)
                      .FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw new InvalidOperationException("Job not found.");

        var wanted = accessoryIds.Distinct().ToList();
        db.JobAccessories.RemoveRange(job.Accessories.Where(a => !wanted.Contains(a.AccessoryId)));
        foreach (var id in wanted.Where(id => job.Accessories.All(a => a.AccessoryId != id)))
            job.Accessories.Add(new JobAccessory { AccessoryId = id });

        await db.SaveChangesAsync(ct);
    }
}
