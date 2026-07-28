using ErpPlatform.Shared.Persistence;
using GatePass.Domain;
using Microsoft.EntityFrameworkCore;

namespace GatePass.Infrastructure;

public record GatePassFilter(
    string? Search = null,
    GatePassDirection? Direction = null,
    GatePassStatus? Status = null,
    bool OutstandingReturnsOnly = false,
    DateOnly? From = null,
    DateOnly? To = null);

public interface IGatePassService
{
    Task<List<GatePassRecord>> ListAsync(GatePassFilter filter, CancellationToken ct = default);
    Task<GatePassRecord?> GetAsync(int id, CancellationToken ct = default);
    Task<GatePassRecord> IssueAsync(GatePassRecord pass, string authorizerId, string authorizerName, CancellationToken ct = default);
    Task<GatePassRecord> UpdateAsync(GatePassRecord pass, CancellationToken ct = default);
    /// <summary>Security passes the goods through the gate.</summary>
    Task<GatePassRecord> CompleteAsync(int id, string byName, CancellationToken ct = default);
    /// <summary>Returnable goods have come back.</summary>
    Task<GatePassRecord> RecordReturnAsync(int id, string byName, CancellationToken ct = default);
    Task<GatePassRecord> CancelAsync(int id, string reason, CancellationToken ct = default);
}

public class GatePassService(GatePassDbContext db) : IGatePassService
{
    public async Task<List<GatePassRecord>> ListAsync(GatePassFilter filter, CancellationToken ct = default)
    {
        var q = db.GatePasses.Include(p => p.Items).AsNoTracking().AsQueryable();

        if (filter.Direction is { } dir) q = q.Where(p => p.Direction == dir);
        if (filter.Status is { } st) q = q.Where(p => p.Status == st);
        if (filter.From is { } from) q = q.Where(p => p.IssuedAtUtc >= from.ToDateTime(TimeOnly.MinValue));
        if (filter.To is { } to) q = q.Where(p => p.IssuedAtUtc <= to.ToDateTime(TimeOnly.MaxValue));

        if (filter.OutstandingReturnsOnly)
            q = q.Where(p => p.IsReturnable && p.ReturnedAtUtc == null
                          && p.Status != GatePassStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(p => p.PassNumber.Contains(s)
                          || p.PersonName.Contains(s)
                          || (p.CompanyName != null && p.CompanyName.Contains(s))
                          || (p.VehicleNumber != null && p.VehicleNumber.Contains(s))
                          || (p.ReferenceNumber != null && p.ReferenceNumber.Contains(s)));
        }

        return await q.OrderByDescending(p => p.Id).Take(500).ToListAsync(ct);
    }

    public Task<GatePassRecord?> GetAsync(int id, CancellationToken ct = default) =>
        db.GatePasses.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<GatePassRecord> IssueAsync(
        GatePassRecord pass, string authorizerId, string authorizerName, CancellationToken ct = default)
    {
        Validate(pass);

        // Inward and outward number in separate series, matching how the gate book reads.
        var (type, prefix) = pass.Direction == GatePassDirection.Inward
            ? ("GatePassIn", "GP-IN")
            : ("GatePassOut", "GP-OUT");

        pass.PassNumber = await new DocumentNumberService(db).NextAsync(type, prefix, ct);
        pass.IssuedAtUtc = DateTime.UtcNow;
        pass.AuthorizedById = authorizerId;
        pass.AuthorizedByName = authorizerName;
        pass.Status = GatePassStatus.Issued;

        db.GatePasses.Add(pass);
        await db.SaveChangesAsync(ct);
        return pass;
    }

    public async Task<GatePassRecord> UpdateAsync(GatePassRecord pass, CancellationToken ct = default)
    {
        var existing = await db.GatePasses.Include(p => p.Items)
                           .FirstOrDefaultAsync(p => p.Id == pass.Id, ct)
                       ?? throw new InvalidOperationException("Gate pass not found.");

        // Once the goods are through the gate the record is a historical fact.
        if (existing.Status != GatePassStatus.Issued)
            throw new InvalidOperationException("Only a pass that hasn't left the gate can be edited.");

        Validate(pass);

        existing.PersonName = pass.PersonName;
        existing.PersonPhone = pass.PersonPhone;
        existing.PersonCnic = pass.PersonCnic;
        existing.CompanyName = pass.CompanyName;
        existing.VehicleNumber = pass.VehicleNumber;
        existing.Department = pass.Department;
        existing.Purpose = pass.Purpose;
        existing.Notes = pass.Notes;
        existing.ReferenceType = pass.ReferenceType;
        existing.ReferenceNumber = pass.ReferenceNumber;
        existing.IsReturnable = pass.IsReturnable;
        existing.ExpectedReturnOn = pass.IsReturnable ? pass.ExpectedReturnOn : null;

        db.GatePassItems.RemoveRange(existing.Items);
        existing.Items = pass.Items.Select(i => new GatePassItem
        {
            Description = i.Description,
            SerialNumber = i.SerialNumber,
            Quantity = i.Quantity,
            Unit = i.Unit,
            Remarks = i.Remarks
        }).ToList();

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<GatePassRecord> CompleteAsync(int id, string byName, CancellationToken ct = default)
    {
        var pass = await Require(id, ct);
        if (pass.Status != GatePassStatus.Issued)
            throw new InvalidOperationException("Only an issued pass can be passed through the gate.");

        pass.Status = GatePassStatus.Completed;
        pass.CompletedAtUtc = DateTime.UtcNow;
        pass.CompletedByName = byName;
        await db.SaveChangesAsync(ct);
        return pass;
    }

    public async Task<GatePassRecord> RecordReturnAsync(int id, string byName, CancellationToken ct = default)
    {
        var pass = await Require(id, ct);
        if (!pass.IsReturnable)
            throw new InvalidOperationException("This pass wasn't issued for returnable goods.");
        if (pass.ReturnedAtUtc is not null)
            throw new InvalidOperationException("The return has already been recorded.");
        if (pass.Status == GatePassStatus.Cancelled)
            throw new InvalidOperationException("This pass was cancelled.");

        pass.ReturnedAtUtc = DateTime.UtcNow;
        pass.ReturnReceivedByName = byName;
        pass.Status = GatePassStatus.Returned;
        await db.SaveChangesAsync(ct);
        return pass;
    }

    public async Task<GatePassRecord> CancelAsync(int id, string reason, CancellationToken ct = default)
    {
        var pass = await Require(id, ct);
        if (pass.Status is GatePassStatus.Completed or GatePassStatus.Returned)
            throw new InvalidOperationException("Goods have already moved; this pass can't be cancelled.");

        pass.Status = GatePassStatus.Cancelled;
        pass.CancelledAtUtc = DateTime.UtcNow;
        pass.CancellationReason = reason;
        await db.SaveChangesAsync(ct);
        return pass;
    }

    private async Task<GatePassRecord> Require(int id, CancellationToken ct) =>
        await db.GatePasses.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new InvalidOperationException("Gate pass not found.");

    private static void Validate(GatePassRecord pass)
    {
        if (string.IsNullOrWhiteSpace(pass.PersonName))
            throw new InvalidOperationException("Name of the person carrying the goods is required.");
        if (string.IsNullOrWhiteSpace(pass.Purpose))
            throw new InvalidOperationException("Purpose is required.");
        if (pass.Items.Count == 0)
            throw new InvalidOperationException("Add at least one item.");
        if (pass.Items.Any(i => string.IsNullOrWhiteSpace(i.Description)))
            throw new InvalidOperationException("Every item needs a description.");
        if (pass.Items.Any(i => i.Quantity <= 0))
            throw new InvalidOperationException("Item quantities must be positive.");
        if (pass.IsReturnable && pass.ExpectedReturnOn is null)
            throw new InvalidOperationException("Returnable goods need an expected return date.");
    }
}
