using Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure;

public record LedgerFilter(
    string? Search = null,
    LedgerStatus? Status = null,
    LedgerNature? Nature = null,
    bool MainOnly = false,
    bool OutstandingOnly = false);

/// <summary>
/// A ledger with its money totalled up. <paramref name="Own"/> is this ledger's own
/// balance; <paramref name="Rollup"/> adds every descendant, which is what tells you
/// how much of a main ledger's money is still out somewhere in its tree.
/// </summary>
public record LedgerBalance(
    int LedgerId,
    decimal Opening,
    decimal TotalIn,
    decimal TotalOut,
    decimal Own,
    decimal Rollup,
    int EntryCount,
    int ChildCount);

/// <summary>One node of the ledger tree, with children nested underneath.</summary>
public record LedgerNode(PlainLedger Ledger, LedgerBalance Balance, List<LedgerNode> Children, int Depth);

/// <summary>A statement line — the entry plus the balance after it.</summary>
public record StatementLine(LedgerEntry Entry, decimal RunningBalance);

public interface ILedgerService
{
    Task<List<PlainLedger>> ListAsync(LedgerFilter filter, CancellationToken ct = default);
    Task<List<LedgerNode>> GetTreeAsync(LedgerFilter? filter = null, CancellationToken ct = default);
    Task<PlainLedger?> GetAsync(int id, CancellationToken ct = default);
    Task<LedgerBalance> GetBalanceAsync(int id, CancellationToken ct = default);
    Task<List<StatementLine>> GetStatementAsync(int id, DateOnly? from = null, DateOnly? to = null,
        CancellationToken ct = default);

    Task<PlainLedger> CreateAsync(PlainLedger ledger, CancellationToken ct = default);
    Task<PlainLedger> UpdateAsync(PlainLedger ledger, CancellationToken ct = default);
    /// <summary>Soft-deletes a ledger. Refused while it has children or entries.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Money crossing the boundary of the tree — cash received or paid out.</summary>
    Task<LedgerEntry> AddEntryAsync(LedgerEntry entry, string userId, string userName,
        CancellationToken ct = default);

    /// <summary>
    /// Moves money from one ledger to another, writing both halves as a linked pair
    /// so the two statements can never disagree.
    /// </summary>
    Task TransferAsync(int fromLedgerId, int toLedgerId, decimal amount, DateOnly date,
        string description, string? reference, LedgerPaymentMethod method,
        string userId, string userName, CancellationToken ct = default);

    Task<LedgerEntry?> GetEntryAsync(int entryId, CancellationToken ct = default);
    /// <summary>Amends an entry. On a transfer both halves move together.</summary>
    Task UpdateEntryAsync(LedgerEntry entry, CancellationToken ct = default);
    /// <summary>Removes an entry. On a transfer both halves go.</summary>
    Task DeleteEntryAsync(int entryId, CancellationToken ct = default);
}

public class LedgerService(LedgerDbContext db, IBookkeepingPoster poster) : ILedgerService
{
    public async Task<List<PlainLedger>> ListAsync(LedgerFilter filter, CancellationToken ct = default)
    {
        var q = db.Ledgers.Include(l => l.Entries).AsNoTracking().AsSplitQuery().AsQueryable();

        if (filter.Status is { } s) q = q.Where(l => l.Status == s);
        if (filter.Nature is { } n) q = q.Where(l => l.Nature == n);
        if (filter.MainOnly) q = q.Where(l => l.ParentLedgerId == null);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var t = filter.Search.Trim();
            q = q.Where(l => l.Name.Contains(t)
                          || l.CounterpartyName.Contains(t)
                          || (l.Reference != null && l.Reference.Contains(t)));
        }

        var list = await q.OrderBy(l => l.Name).ToListAsync(ct);

        if (filter.OutstandingOnly)
            list = list.Where(l => Own(l) != 0).ToList();

        return list;
    }

    public async Task<List<LedgerNode>> GetTreeAsync(
        LedgerFilter? filter = null, CancellationToken ct = default)
    {
        // The whole tree is loaded once and assembled in memory: a recursive walk
        // would be a query per node, and these books are small (hundreds, not
        // millions) so one pass is both simpler and faster.
        var all = await db.Ledgers.Include(l => l.Entries).AsNoTracking().AsSplitQuery()
            .OrderBy(l => l.Name).ToListAsync(ct);

        var byParent = all.GroupBy(l => l.ParentLedgerId)
            .ToDictionary(g => g.Key ?? 0, g => g.ToList());

        var search = filter?.Search?.Trim();
        var roots = byParent.GetValueOrDefault(0, []);

        if (filter?.Nature is { } nature) roots = roots.Where(l => l.Nature == nature).ToList();
        if (filter?.Status is { } status) roots = roots.Where(l => l.Status == status).ToList();
        if (!string.IsNullOrWhiteSpace(search))
            roots = roots.Where(l => l.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                  || l.CounterpartyName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return roots.Select(r => Build(r, 0)).ToList();

        LedgerNode Build(PlainLedger ledger, int depth)
        {
            var children = byParent.GetValueOrDefault(ledger.Id, [])
                .Select(c => Build(c, depth + 1)).ToList();

            var own = Own(ledger);
            var balance = new LedgerBalance(
                ledger.Id,
                ledger.OpeningBalance,
                ledger.Entries.Where(e => e.Direction == LedgerDirection.In).Sum(e => e.Amount),
                ledger.Entries.Where(e => e.Direction == LedgerDirection.Out).Sum(e => e.Amount),
                own,
                own + children.Sum(c => c.Balance.Rollup),
                ledger.Entries.Count,
                children.Count);

            return new LedgerNode(ledger, balance, children, depth);
        }
    }

    public Task<PlainLedger?> GetAsync(int id, CancellationToken ct = default) =>
        db.Ledgers
            .Include(l => l.ParentLedger)
            .Include(l => l.Children)
            .Include(l => l.Entries)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<LedgerBalance> GetBalanceAsync(int id, CancellationToken ct = default)
    {
        var tree = await GetTreeAsync(ct: ct);
        var found = Find(tree);
        if (found is not null) return found;

        // A sub-ledger reached directly isn't a root, so walk in from the roots.
        throw new InvalidOperationException("Ledger not found.");

        LedgerBalance? Find(IEnumerable<LedgerNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Ledger.Id == id) return n.Balance;
                if (Find(n.Children) is { } hit) return hit;
            }
            return null;
        }
    }

    public async Task<List<StatementLine>> GetStatementAsync(
        int id, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var ledger = await db.Ledgers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new InvalidOperationException("Ledger not found.");

        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.PlainLedgerId == id)
            .OrderBy(e => e.Date).ThenBy(e => e.Id)
            .ToListAsync(ct);

        // The running balance always starts from the opening figure and walks every
        // entry, then the window is applied — otherwise a date filter would show a
        // running total that doesn't match the ledger's real balance.
        var running = ledger.OpeningBalance;
        var lines = new List<StatementLine>();
        foreach (var e in entries)
        {
            running += e.Signed;
            if (from is { } f && e.Date < f) continue;
            if (to is { } t && e.Date > t) continue;
            lines.Add(new StatementLine(e, running));
        }

        return lines;
    }

    public async Task<PlainLedger> CreateAsync(PlainLedger ledger, CancellationToken ct = default)
    {
        Validate(ledger);

        if (ledger.ParentLedgerId is { } parentId)
        {
            var parent = await db.Ledgers.FirstOrDefaultAsync(l => l.Id == parentId, ct)
                ?? throw new InvalidOperationException("Parent ledger not found.");
            if (parent.Status != LedgerStatus.Open)
                throw new InvalidOperationException(
                    $"{parent.Name} is {parent.Status.ToString().ToLowerInvariant()}; " +
                    "a sub-ledger can only be opened under an open ledger.");
        }

        if (ledger.OpenedOn == default) ledger.OpenedOn = DateOnly.FromDateTime(DateTime.UtcNow);

        db.Ledgers.Add(ledger);
        await db.SaveChangesAsync(ct);
        return ledger;
    }

    public async Task<PlainLedger> UpdateAsync(PlainLedger ledger, CancellationToken ct = default)
    {
        Validate(ledger);

        var existing = await db.Ledgers.FirstOrDefaultAsync(l => l.Id == ledger.Id, ct)
            ?? throw new InvalidOperationException("Ledger not found.");

        // Re-parenting is allowed, but never onto itself or one of its own
        // descendants — that would build a cycle the tree walk can't get out of.
        //
        // Checked unconditionally rather than only when the parent looks changed:
        // a caller that loaded this ledger from the same DbContext hands back the
        // very instance EF is already tracking, so `existing` and `ledger` are one
        // object and any "has it changed?" comparison is a field against itself.
        if (ledger.ParentLedgerId is { } newParent)
        {
            if (newParent == ledger.Id)
                throw new InvalidOperationException("A ledger can't be its own parent.");
            if (await IsDescendantAsync(newParent, ledger.Id, ct))
                throw new InvalidOperationException(
                    "That would put the ledger under one of its own sub-ledgers.");
        }

        existing.Name = ledger.Name;
        existing.CounterpartyName = ledger.CounterpartyName;
        existing.CounterpartyPhone = ledger.CounterpartyPhone;
        existing.CounterpartyAddress = ledger.CounterpartyAddress;
        existing.Nature = ledger.Nature;
        existing.ParentLedgerId = ledger.ParentLedgerId;
        existing.OpeningBalance = ledger.OpeningBalance;
        existing.OpenedOn = ledger.OpenedOn;
        existing.Status = ledger.Status;
        existing.Reference = ledger.Reference;
        existing.Notes = ledger.Notes;
        existing.FinanceAccountId = ledger.FinanceAccountId;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var ledger = await db.Ledgers
            .Include(l => l.Children)
            .Include(l => l.Entries)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (ledger is null) return;

        if (ledger.Children.Count > 0)
            throw new InvalidOperationException(
                $"{ledger.Name} has {ledger.Children.Count} sub-ledger(s). Remove or move them first.");
        if (ledger.Entries.Count > 0)
            throw new InvalidOperationException(
                $"{ledger.Name} has {ledger.Entries.Count} entr(y/ies). " +
                "Close it instead — deleting would lose the record of the money.");

        db.Ledgers.Remove(ledger);
        await db.SaveChangesAsync(ct);
    }

    public async Task<LedgerEntry> AddEntryAsync(
        LedgerEntry entry, string userId, string userName, CancellationToken ct = default)
    {
        if (entry.Amount <= 0)
            throw new InvalidOperationException("Amount must be positive.");
        if (string.IsNullOrWhiteSpace(entry.Description))
            throw new InvalidOperationException("Description is required.");

        var ledger = await db.Ledgers.FirstOrDefaultAsync(l => l.Id == entry.PlainLedgerId, ct)
            ?? throw new InvalidOperationException("Ledger not found.");
        if (ledger.Status != LedgerStatus.Open)
            throw new InvalidOperationException(
                $"{ledger.Name} is {ledger.Status.ToString().ToLowerInvariant()}; reopen it to add entries.");

        entry.Kind = LedgerEntryKind.External;
        entry.CounterLedgerId = null;
        entry.TransferGroup = null;
        entry.RecordedById = userId;
        entry.RecordedByName = userName;
        if (entry.Date == default) entry.Date = DateOnly.FromDateTime(DateTime.UtcNow);

        db.Entries.Add(entry);
        await db.SaveChangesAsync(ct);

        entry.PostedVoucherId = await PostAsync(ledger, entry, ct);
        if (entry.PostedVoucherId is not null) await db.SaveChangesAsync(ct);

        return entry;
    }

    public async Task TransferAsync(
        int fromLedgerId, int toLedgerId, decimal amount, DateOnly date,
        string description, string? reference, LedgerPaymentMethod method,
        string userId, string userName, CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new InvalidOperationException("Amount must be positive.");
        if (fromLedgerId == toLedgerId)
            throw new InvalidOperationException("Pick two different ledgers.");
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Description is required.");

        var from = await db.Ledgers.FirstOrDefaultAsync(l => l.Id == fromLedgerId, ct)
            ?? throw new InvalidOperationException("Source ledger not found.");
        var to = await db.Ledgers.FirstOrDefaultAsync(l => l.Id == toLedgerId, ct)
            ?? throw new InvalidOperationException("Destination ledger not found.");

        foreach (var l in new[] { from, to })
            if (l.Status != LedgerStatus.Open)
                throw new InvalidOperationException(
                    $"{l.Name} is {l.Status.ToString().ToLowerInvariant()}; reopen it to transfer.");

        if (date == default) date = DateOnly.FromDateTime(DateTime.UtcNow);
        var group = Guid.NewGuid();

        var outEntry = new LedgerEntry
        {
            PlainLedgerId = from.Id, Date = date, Direction = LedgerDirection.Out,
            Kind = LedgerEntryKind.Transfer, Amount = amount,
            Description = description, Reference = reference, Method = method,
            CounterLedgerId = to.Id, TransferGroup = group,
            RecordedById = userId, RecordedByName = userName
        };
        var inEntry = new LedgerEntry
        {
            PlainLedgerId = to.Id, Date = date, Direction = LedgerDirection.In,
            Kind = LedgerEntryKind.Transfer, Amount = amount,
            Description = description, Reference = reference, Method = method,
            CounterLedgerId = from.Id, TransferGroup = group,
            RecordedById = userId, RecordedByName = userName
        };

        // Both halves in one SaveChanges: a transfer that wrote only one side would
        // leave the two statements permanently disagreeing.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Entries.AddRange(outEntry, inEntry);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        // Only the receiving side posts. Cash physically moves once, so posting both
        // halves would double-count it, and the source counterparty's balance is
        // unaffected by what you do with money once you hold it.
        var voucherId = await PostAsync(to, inEntry, ct);
        if (voucherId is not null)
        {
            inEntry.PostedVoucherId = voucherId;
            await db.SaveChangesAsync(ct);
        }
    }

    public Task<LedgerEntry?> GetEntryAsync(int entryId, CancellationToken ct = default) =>
        db.Entries.Include(e => e.PlainLedger).Include(e => e.CounterLedger)
            .FirstOrDefaultAsync(e => e.Id == entryId, ct);

    public async Task UpdateEntryAsync(LedgerEntry entry, CancellationToken ct = default)
    {
        if (entry.Amount <= 0)
            throw new InvalidOperationException("Amount must be positive.");
        if (string.IsNullOrWhiteSpace(entry.Description))
            throw new InvalidOperationException("Description is required.");

        var existing = await db.Entries.FirstOrDefaultAsync(e => e.Id == entry.Id, ct)
            ?? throw new InvalidOperationException("Entry not found.");

        var halves = existing.TransferGroup is { } g
            ? await db.Entries.Where(e => e.TransferGroup == g).ToListAsync(ct)
            : [existing];

        // The shared facts move on both halves; direction and which ledger each
        // side sits on are what make it a transfer and stay put.
        foreach (var half in halves)
        {
            half.Date = entry.Date;
            half.Amount = entry.Amount;
            half.Description = entry.Description;
            half.Reference = entry.Reference;
            half.Method = entry.Method;
        }

        if (halves.Count == 1) existing.Direction = entry.Direction;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteEntryAsync(int entryId, CancellationToken ct = default)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null) return;

        var halves = entry.TransferGroup is { } g
            ? await db.Entries.Where(e => e.TransferGroup == g).ToListAsync(ct)
            : [entry];

        db.Entries.RemoveRange(halves);
        await db.SaveChangesAsync(ct);
    }

    // --- helpers ---

    private static decimal Own(PlainLedger l) =>
        l.OpeningBalance + l.Entries.Sum(e => e.Signed);

    private static void Validate(PlainLedger ledger)
    {
        if (string.IsNullOrWhiteSpace(ledger.Name))
            throw new InvalidOperationException("Ledger name is required.");
        if (string.IsNullOrWhiteSpace(ledger.CounterpartyName))
            throw new InvalidOperationException("Counterparty name is required.");
    }

    /// <summary>True when <paramref name="candidateId"/> sits under <paramref name="ancestorId"/>.</summary>
    private async Task<bool> IsDescendantAsync(int candidateId, int ancestorId, CancellationToken ct)
    {
        var current = await db.Ledgers.AsNoTracking()
            .Where(l => l.Id == candidateId)
            .Select(l => l.ParentLedgerId).FirstOrDefaultAsync(ct);

        // Bounded walk: a corrupt row that somehow points into a cycle must not
        // spin here forever.
        for (var hops = 0; current is { } id && hops < 64; hops++)
        {
            if (id == ancestorId) return true;
            current = await db.Ledgers.AsNoTracking()
                .Where(l => l.Id == id).Select(l => l.ParentLedgerId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    /// <summary>
    /// Mirrors an entry into the formal books, when this ledger is mapped to an
    /// account and a cash account is configured. Silent no-op otherwise — an
    /// unmapped ledger is a perfectly valid plain ledger.
    /// </summary>
    /// <remarks>
    /// The direction depends on the ledger's <see cref="LedgerNature"/>, because
    /// "In" means opposite things either side of a tree:
    /// <list type="bullet">
    /// <item>Payable, In — you took their money: Dr Cash, Cr them (you owe more).</item>
    /// <item>Payable, Out — you paid them back: Dr them, Cr Cash.</item>
    /// <item>Receivable, In — you handed money over: Dr them (they owe more), Cr Cash.</item>
    /// <item>Receivable, Out — they returned money: Dr Cash, Cr them.</item>
    /// </list>
    /// </remarks>
    private async Task<int?> PostAsync(PlainLedger ledger, LedgerEntry entry, CancellationToken ct)
    {
        if (!poster.IsAvailable || ledger.FinanceAccountId is not { } accountId) return null;

        var cashAccountId = await GetCashAccountIdAsync(ct);
        if (cashAccountId is not { } cash) return null;

        var cashIn = ledger.Nature == LedgerNature.Payable
            ? entry.Direction == LedgerDirection.In
            : entry.Direction == LedgerDirection.Out;

        var lines = cashIn
            ? new[]
            {
                new BookkeepingLine(cash, entry.Amount, 0, entry.Description),
                new BookkeepingLine(accountId, 0, entry.Amount, ledger.CounterpartyName)
            }
            :
            [
                new BookkeepingLine(accountId, entry.Amount, 0, ledger.CounterpartyName),
                new BookkeepingLine(cash, 0, entry.Amount, entry.Description)
            ];

        try
        {
            return await poster.PostAsync(
                entry.Date,
                $"{ledger.Name}: {entry.Description}",
                "ledger", entry.Id, lines, ct);
        }
        catch (Exception)
        {
            // The plain ledger is the record of record here. If accounting refuses
            // the journal (a locked period, a since-deleted account), the entry
            // still stands with PostedVoucherId null so the gap is visible and can
            // be re-posted, rather than the user losing the entry entirely.
            return null;
        }
    }

    private async Task<int?> GetCashAccountIdAsync(CancellationToken ct)
    {
        var raw = await db.Settings.AsNoTracking()
            .Where(s => s.Key == LedgerSettingKeys.CashAccountId)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var id) ? id : null;
    }
}
