using Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure;

/// <summary>A head with its own children nested underneath, and what sits under it.</summary>
public record HeadNode(LedgerHead Head, List<HeadNode> Children, int Depth);

/// <summary>
/// Money filed under a head. <paramref name="OwnIn"/>/<paramref name="OwnOut"/> count
/// only entries tagged with this head; the rollup figures add every sub-head.
/// </summary>
public record HeadTotals(
    int HeadId,
    decimal OwnIn,
    decimal OwnOut,
    decimal RollupIn,
    decimal RollupOut,
    int OwnEntryCount,
    int LedgerCount);

public interface ILedgerHeadService
{
    Task<List<LedgerHead>> ListAsync(bool activeOnly = false, CancellationToken ct = default);
    Task<List<HeadNode>> GetTreeAsync(CancellationToken ct = default);
    Task<LedgerHead?> GetAsync(int id, CancellationToken ct = default);
    Task<LedgerHead> SaveAsync(LedgerHead head, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a head. Refused while it has sub-heads. Ledgers and entries
    /// filed under it are left alone — the foreign key nulls out, so they read as
    /// unclassified rather than disappearing with the head.
    /// </summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Totals per head, for the head report. Keyed by head id.</summary>
    Task<Dictionary<int, HeadTotals>> GetTotalsAsync(CancellationToken ct = default);
}

public class LedgerHeadService(LedgerDbContext db) : ILedgerHeadService
{
    public async Task<List<LedgerHead>> ListAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var q = db.Heads.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(h => h.IsActive);
        return await q.OrderBy(h => h.Name).ToListAsync(ct);
    }

    public async Task<List<HeadNode>> GetTreeAsync(CancellationToken ct = default)
    {
        var all = await db.Heads.AsNoTracking().OrderBy(h => h.Name).ToListAsync(ct);
        var byParent = all.GroupBy(h => h.ParentHeadId ?? 0)
            .ToDictionary(g => g.Key, g => g.ToList());

        return byParent.GetValueOrDefault(0, []).Select(h => Build(h, 0)).ToList();

        HeadNode Build(LedgerHead head, int depth) => new(
            head,
            byParent.GetValueOrDefault(head.Id, []).Select(c => Build(c, depth + 1)).ToList(),
            depth);
    }

    public Task<LedgerHead?> GetAsync(int id, CancellationToken ct = default) =>
        db.Heads.Include(h => h.Children).FirstOrDefaultAsync(h => h.Id == id, ct);

    public async Task<LedgerHead> SaveAsync(LedgerHead head, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(head.Name))
            throw new InvalidOperationException("Head name is required.");

        if (head.ParentHeadId is { } parentId)
        {
            if (parentId == head.Id)
                throw new InvalidOperationException("A head can't be its own parent.");
            // Checked unconditionally: a caller that loaded this head from the same
            // DbContext hands back the instance EF already tracks, so any "did the
            // parent change?" test would compare a field against itself.
            if (head.Id != 0 && await IsDescendantAsync(parentId, head.Id, ct))
                throw new InvalidOperationException(
                    "That would put the head under one of its own sub-heads.");
        }

        if (head.Id == 0)
        {
            db.Heads.Add(head);
            await db.SaveChangesAsync(ct);
            return head;
        }

        var existing = await db.Heads.FirstOrDefaultAsync(h => h.Id == head.Id, ct)
            ?? throw new InvalidOperationException("Head not found.");

        existing.Name = head.Name;
        existing.Code = head.Code;
        existing.ParentHeadId = head.ParentHeadId;
        existing.Notes = head.Notes;
        existing.IsActive = head.IsActive;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var head = await db.Heads.Include(h => h.Children)
            .FirstOrDefaultAsync(h => h.Id == id, ct);
        if (head is null) return;

        if (head.Children.Count > 0)
            throw new InvalidOperationException(
                $"{head.Name} has {head.Children.Count} sub-head(s). Remove or move them first.");

        // Ledgers and entries keep their money and simply lose the classification.
        foreach (var ledger in await db.Ledgers.Where(l => l.HeadId == id).ToListAsync(ct))
            ledger.HeadId = null;
        foreach (var entry in await db.Entries.Where(e => e.HeadId == id).ToListAsync(ct))
            entry.HeadId = null;

        db.Heads.Remove(head);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<int, HeadTotals>> GetTotalsAsync(CancellationToken ct = default)
    {
        var heads = await db.Heads.AsNoTracking().ToListAsync(ct);

        var entryTotals = await db.Entries.AsNoTracking()
            .Where(e => e.HeadId != null)
            .GroupBy(e => new { HeadId = e.HeadId!.Value, e.Direction })
            .Select(g => new { g.Key.HeadId, g.Key.Direction, Total = g.Sum(x => x.Amount), Count = g.Count() })
            .ToListAsync(ct);

        var ledgerCounts = await db.Ledgers.AsNoTracking()
            .Where(l => l.HeadId != null)
            .GroupBy(l => l.HeadId!.Value)
            .Select(g => new { HeadId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byParent = heads.GroupBy(h => h.ParentHeadId ?? 0)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<int, HeadTotals>();
        foreach (var root in byParent.GetValueOrDefault(0, [])) Walk(root);
        return result;

        // Depth-first so a parent's rollup can add children already computed.
        HeadTotals Walk(LedgerHead head)
        {
            var ownIn = entryTotals
                .Where(t => t.HeadId == head.Id && t.Direction == LedgerDirection.In)
                .Sum(t => t.Total);
            var ownOut = entryTotals
                .Where(t => t.HeadId == head.Id && t.Direction == LedgerDirection.Out)
                .Sum(t => t.Total);
            var ownCount = entryTotals.Where(t => t.HeadId == head.Id).Sum(t => t.Count);

            var rollupIn = ownIn;
            var rollupOut = ownOut;
            foreach (var child in byParent.GetValueOrDefault(head.Id, []))
            {
                var childTotals = Walk(child);
                rollupIn += childTotals.RollupIn;
                rollupOut += childTotals.RollupOut;
            }

            var totals = new HeadTotals(
                head.Id, ownIn, ownOut, rollupIn, rollupOut, ownCount,
                ledgerCounts.FirstOrDefault(c => c.HeadId == head.Id)?.Count ?? 0);

            result[head.Id] = totals;
            return totals;
        }
    }

    private async Task<bool> IsDescendantAsync(int candidateId, int ancestorId, CancellationToken ct)
    {
        var current = await db.Heads.AsNoTracking()
            .Where(h => h.Id == candidateId).Select(h => h.ParentHeadId).FirstOrDefaultAsync(ct);

        for (var hops = 0; current is { } id && hops < 64; hops++)
        {
            if (id == ancestorId) return true;
            current = await db.Heads.AsNoTracking()
                .Where(h => h.Id == id).Select(h => h.ParentHeadId).FirstOrDefaultAsync(ct);
        }
        return false;
    }
}
