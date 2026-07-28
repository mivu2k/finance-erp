using ErpPlatform.Shared.Persistence;
using GatePass.Domain;
using Microsoft.EntityFrameworkCore;

namespace GatePass.Infrastructure;

public record DemoFilter(
    string? Search = null,
    DemoStatus? Status = null,
    bool OutstandingOnly = false,
    bool OverdueOnly = false);

public interface IDemoIssuanceService
{
    Task<List<DemoIssuance>> ListAsync(DemoFilter filter, CancellationToken ct = default);
    Task<DemoIssuance?> GetAsync(int id, CancellationToken ct = default);
    Task<DemoIssuance> IssueAsync(DemoIssuance issuance, string issuerId, string issuerName, CancellationToken ct = default);
    Task<DemoIssuance> UpdateAsync(DemoIssuance issuance, CancellationToken ct = default);
    /// <summary>Marks the listed items back. Omitting <paramref name="itemIds"/> returns everything.</summary>
    Task<DemoIssuance> RecordReturnAsync(int id, IEnumerable<int>? itemIds, string receivedByName,
        string? condition, CancellationToken ct = default);
    Task<DemoIssuance> CancelAsync(int id, CancellationToken ct = default);
}

public class DemoIssuanceService(GatePassDbContext db) : IDemoIssuanceService
{
    public async Task<List<DemoIssuance>> ListAsync(DemoFilter filter, CancellationToken ct = default)
    {
        var q = db.DemoIssuances.Include(d => d.Items).AsNoTracking().AsQueryable();

        if (filter.Status is { } st) q = q.Where(d => d.Status == st);

        if (filter.OutstandingOnly || filter.OverdueOnly)
            q = q.Where(d => d.Status == DemoStatus.Issued || d.Status == DemoStatus.PartiallyReturned);

        if (filter.OverdueOnly)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            q = q.Where(d => d.ExpectedReturnOn != null && d.ExpectedReturnOn < today);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(d => d.IssuanceNumber.Contains(s)
                          || d.CustomerName.Contains(s)
                          || (d.CustomerPhone != null && d.CustomerPhone.Contains(s))
                          || (d.ReferenceLetter != null && d.ReferenceLetter.Contains(s)));
        }

        return await q.OrderByDescending(d => d.Id).Take(500).ToListAsync(ct);
    }

    public Task<DemoIssuance?> GetAsync(int id, CancellationToken ct = default) =>
        db.DemoIssuances.Include(d => d.Items).FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<DemoIssuance> IssueAsync(
        DemoIssuance issuance, string issuerId, string issuerName, CancellationToken ct = default)
    {
        Validate(issuance);

        issuance.IssuanceNumber = await new DocumentNumberService(db).NextAsync("DemoIssuance", "DEMO", ct);
        issuance.IssuedAtUtc = DateTime.UtcNow;
        issuance.IssuedById = issuerId;
        issuance.IssuedByName = issuerName;
        issuance.Status = DemoStatus.Issued;

        db.DemoIssuances.Add(issuance);
        await db.SaveChangesAsync(ct);
        return issuance;
    }

    public async Task<DemoIssuance> UpdateAsync(DemoIssuance issuance, CancellationToken ct = default)
    {
        var existing = await db.DemoIssuances.Include(d => d.Items)
                           .FirstOrDefaultAsync(d => d.Id == issuance.Id, ct)
                       ?? throw new InvalidOperationException("Demo issuance not found.");

        if (existing.Status is not DemoStatus.Issued)
            throw new InvalidOperationException("Only an outstanding issuance can be edited.");

        Validate(issuance);

        existing.CustomerName = issuance.CustomerName;
        existing.CustomerPhone = issuance.CustomerPhone;
        existing.CustomerReference = issuance.CustomerReference;
        existing.Department = issuance.Department;
        existing.ReferenceLetter = issuance.ReferenceLetter;
        existing.ExpectedReturnOn = issuance.ExpectedReturnOn;
        existing.Notes = issuance.Notes;

        db.DemoIssuanceItems.RemoveRange(existing.Items);
        existing.Items = issuance.Items.Select(i => new DemoIssuanceItem
        {
            Description = i.Description,
            SerialNumber = i.SerialNumber,
            Quantity = i.Quantity,
            Accessories = i.Accessories,
            Remarks = i.Remarks
        }).ToList();

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<DemoIssuance> RecordReturnAsync(
        int id, IEnumerable<int>? itemIds, string receivedByName,
        string? condition, CancellationToken ct = default)
    {
        var issuance = await db.DemoIssuances.Include(d => d.Items)
                           .FirstOrDefaultAsync(d => d.Id == id, ct)
                       ?? throw new InvalidOperationException("Demo issuance not found.");

        if (issuance.Status is DemoStatus.Returned or DemoStatus.Cancelled)
            throw new InvalidOperationException("This issuance is already closed.");

        var now = DateTime.UtcNow;
        var targets = itemIds?.ToHashSet();

        foreach (var item in issuance.Items.Where(i => i.ReturnedAtUtc is null))
            if (targets is null || targets.Contains(item.Id))
                item.ReturnedAtUtc = now;

        // Fully returned only once every item is back — otherwise it stays open.
        var outstanding = issuance.Items.Count(i => i.ReturnedAtUtc is null);
        if (outstanding == 0)
        {
            issuance.Status = DemoStatus.Returned;
            issuance.ReturnedAtUtc = now;
            issuance.ReceivedByName = receivedByName;
            issuance.ReturnCondition = condition;
        }
        else
        {
            issuance.Status = DemoStatus.PartiallyReturned;
            issuance.ReceivedByName = receivedByName;
        }

        await db.SaveChangesAsync(ct);
        return issuance;
    }

    public async Task<DemoIssuance> CancelAsync(int id, CancellationToken ct = default)
    {
        var issuance = await db.DemoIssuances.FirstOrDefaultAsync(d => d.Id == id, ct)
                       ?? throw new InvalidOperationException("Demo issuance not found.");
        if (issuance.Status == DemoStatus.Returned)
            throw new InvalidOperationException("A completed issuance can't be cancelled.");

        issuance.Status = DemoStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return issuance;
    }

    private static void Validate(DemoIssuance issuance)
    {
        if (string.IsNullOrWhiteSpace(issuance.CustomerName))
            throw new InvalidOperationException("Customer name is required.");
        if (issuance.Items.Count == 0)
            throw new InvalidOperationException("Add at least one item.");
        if (issuance.Items.Any(i => string.IsNullOrWhiteSpace(i.Description)))
            throw new InvalidOperationException("Every item needs a description.");
        if (issuance.Items.Any(i => i.Quantity <= 0))
            throw new InvalidOperationException("Item quantities must be positive.");
    }
}
