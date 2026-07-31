using ErpPlatform.TestSupport;
using ErpPlatform.Shared.Kernel;
using Ledger.Domain;
using Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ledger.Tests;

/// <summary>
/// Walks the scenario the module was built for: 100,000 taken from Mr A, then
/// 50,000 each passed to Mr B and Mr C, with repayments coming back afterwards.
/// The transfer pairing and the tree rollup are what make the book readable, so
/// they are what these pin down.
/// </summary>
public class PlainLedgerTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_ledger_test_{Guid.NewGuid():N}"[..28];
    private bool _available;

    private DbContextOptions<LedgerDbContext> Opts() =>
        new DbContextOptionsBuilder<LedgerDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

    private LedgerDbContext NewDb() => new(Opts(), new TestUser());
    private LedgerService NewService(LedgerDbContext db) => new(db);

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

    private static PlainLedger MainLedger(string name, string party) => new()
    {
        Name = name, CounterpartyName = party,
        Nature = LedgerNature.Payable,
        OpenedOn = new DateOnly(2026, 7, 1)
    };

    private static PlainLedger Sub(string name, string party, int parentId) => new()
    {
        Name = name, CounterpartyName = party,
        Nature = LedgerNature.Receivable, ParentLedgerId = parentId,
        OpenedOn = new DateOnly(2026, 7, 1)
    };

    /// <summary>Builds the whole worked example and returns the three ledger ids.</summary>
    private async Task<(int MainId, int BId, int CId)> SeedScenarioAsync()
    {
        await using var db = NewDb();
        var svc = NewService(db);

        var main = await svc.CreateAsync(MainLedger("Mr A — 1 lac taken", "Mr A"));
        var b = await svc.CreateAsync(Sub("Mr B — 50k", "Mr B", main.Id));
        var c = await svc.CreateAsync(Sub("Mr C — 50k", "Mr C", main.Id));

        // Took 100,000 from Mr A: money into the main pot.
        await svc.AddEntryAsync(new LedgerEntry
        {
            PlainLedgerId = main.Id, Date = new DateOnly(2026, 7, 1),
            Direction = LedgerDirection.In, Amount = 100_000,
            Description = "Cash taken from Mr A"
        }, "u1", "Tester");

        // Passed 50,000 to each of B and C.
        await svc.TransferAsync(main.Id, b.Id, 50_000, new DateOnly(2026, 7, 2),
            "Advance to Mr B", null, LedgerPaymentMethod.Cash, "u1", "Tester");
        await svc.TransferAsync(main.Id, c.Id, 50_000, new DateOnly(2026, 7, 2),
            "Advance to Mr C", null, LedgerPaymentMethod.Cash, "u1", "Tester");

        return (main.Id, b.Id, c.Id);
    }

    [SkippableFact]
    public async Task A_transfer_writes_both_halves_as_a_linked_pair()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.Kind == LedgerEntryKind.Transfer).ToListAsync();

        // Two transfers, so four halves, in two groups of two.
        Assert.Equal(4, entries.Count);
        var groups = entries.GroupBy(e => e.TransferGroup).ToList();
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Count()));

        // Each pair is one Out on the source and one In on the destination, and the
        // two point at each other.
        var pair = groups.First(g => g.Any(e => e.PlainLedgerId == bId));
        var outHalf = pair.Single(e => e.Direction == LedgerDirection.Out);
        var inHalf = pair.Single(e => e.Direction == LedgerDirection.In);

        Assert.Equal(mainId, outHalf.PlainLedgerId);
        Assert.Equal(bId, inHalf.PlainLedgerId);
        Assert.Equal(bId, outHalf.CounterLedgerId);
        Assert.Equal(mainId, inHalf.CounterLedgerId);
        Assert.Equal(outHalf.Amount, inHalf.Amount);
    }

    [SkippableFact]
    public async Task Fully_distributing_a_main_ledger_leaves_it_unallocated_nil_but_the_tree_intact()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, _, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var tree = await NewService(db).GetTreeAsync();
        var main = tree.Single(n => n.Ledger.Id == mainId);

        // 100,000 in, 50,000 + 50,000 out: nothing left unallocated on the main.
        Assert.Equal(0, main.Balance.Own);

        // But the whole tree still holds the 100,000 — it's out with B and C, which
        // is exactly what you still owe Mr A.
        Assert.Equal(100_000, main.Balance.Rollup);
        Assert.Equal(2, main.Children.Count);
        Assert.All(main.Children, c => Assert.Equal(50_000, c.Balance.Own));
    }

    [SkippableFact]
    public async Task A_repayment_reduces_the_sub_ledger_and_the_tree_total()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        // Mr B returns 20,000 — money leaves his pot back to you.
        await svc.AddEntryAsync(new LedgerEntry
        {
            PlainLedgerId = bId, Date = new DateOnly(2026, 7, 10),
            Direction = LedgerDirection.Out, Amount = 20_000,
            Description = "Repayment from Mr B"
        }, "u1", "Tester");

        var tree = await svc.GetTreeAsync();
        var main = tree.Single(n => n.Ledger.Id == mainId);
        var b = main.Children.Single(c => c.Ledger.Id == bId);

        Assert.Equal(30_000, b.Balance.Own);
        Assert.Equal(80_000, main.Balance.Rollup);
    }

    [SkippableFact]
    public async Task Nesting_goes_deeper_than_two_levels()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        // B passes 25,000 of it on to Mr D.
        var d = await svc.CreateAsync(Sub("Mr D — 25k", "Mr D", bId));
        await svc.TransferAsync(bId, d.Id, 25_000, new DateOnly(2026, 7, 5),
            "Passed on to Mr D", null, LedgerPaymentMethod.Cash, "u1", "Tester");

        var tree = await svc.GetTreeAsync();
        var main = tree.Single(n => n.Ledger.Id == mainId);
        var b = main.Children.Single(c => c.Ledger.Id == bId);

        // main is depth 0, B is its child at 1, D sits under B at 2.
        Assert.Equal(1, b.Depth);
        Assert.Single(b.Children);
        Assert.Equal(2, b.Children[0].Depth);
        Assert.Equal(25_000, b.Balance.Own);
        Assert.Equal(50_000, b.Balance.Rollup);
        // The grandchild still counts toward the main ledger's tree total.
        Assert.Equal(100_000, main.Balance.Rollup);
    }

    [SkippableFact]
    public async Task Deleting_one_half_of_a_transfer_removes_the_other()
    {
        IntegrationDatabase.Require(_available);
        var (_, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        var half = await db.Entries.AsNoTracking()
            .FirstAsync(e => e.PlainLedgerId == bId && e.Kind == LedgerEntryKind.Transfer);
        await svc.DeleteEntryAsync(half.Id);

        // The pair goes together — a one-sided transfer would leave the two
        // statements permanently disagreeing.
        var left = await db.Entries.AsNoTracking()
            .CountAsync(e => e.TransferGroup == half.TransferGroup);
        Assert.Equal(0, left);
    }

    [SkippableFact]
    public async Task Amending_one_half_of_a_transfer_moves_both()
    {
        IntegrationDatabase.Require(_available);
        var (_, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        var half = await db.Entries.AsNoTracking()
            .FirstAsync(e => e.PlainLedgerId == bId && e.Kind == LedgerEntryKind.Transfer);

        half.Amount = 40_000;
        half.Description = "Corrected advance";
        await svc.UpdateEntryAsync(half);

        var both = await db.Entries.AsNoTracking()
            .Where(e => e.TransferGroup == half.TransferGroup).ToListAsync();
        Assert.Equal(2, both.Count);
        Assert.All(both, e => Assert.Equal(40_000, e.Amount));
        Assert.All(both, e => Assert.Equal("Corrected advance", e.Description));
    }

    [SkippableFact]
    public async Task The_statement_running_balance_ends_at_the_ledger_balance()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, _, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);
        var statement = await svc.GetStatementAsync(mainId);

        Assert.Equal(3, statement.Count);
        Assert.Equal(100_000, statement[0].RunningBalance);
        Assert.Equal(50_000, statement[1].RunningBalance);
        Assert.Equal(0, statement[2].RunningBalance);
    }

    [SkippableFact]
    public async Task An_opening_balance_starts_the_running_balance()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = NewService(db);
        var l = await svc.CreateAsync(new PlainLedger
        {
            Name = "Carried over", CounterpartyName = "Mr E",
            Nature = LedgerNature.Receivable, OpeningBalance = 15_000,
            OpenedOn = new DateOnly(2026, 7, 1)
        });

        await svc.AddEntryAsync(new LedgerEntry
        {
            PlainLedgerId = l.Id, Date = new DateOnly(2026, 7, 3),
            Direction = LedgerDirection.Out, Amount = 5_000, Description = "Part repaid"
        }, "u1", "Tester");

        var statement = await svc.GetStatementAsync(l.Id);
        Assert.Equal(10_000, statement.Single().RunningBalance);
    }

    [SkippableFact]
    public async Task A_ledger_cannot_be_reparented_under_its_own_descendant()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        var main = await svc.GetAsync(mainId);
        main!.ParentLedgerId = bId;

        // Otherwise the tree walk would have no way out.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateAsync(main));
    }

    [SkippableFact]
    public async Task A_ledger_with_entries_or_children_refuses_deletion()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        // Main has both children and entries.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync(mainId));
        // B has a transfer half on it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync(bId));

        Assert.NotNull(await svc.GetAsync(mainId));
    }

    [SkippableFact]
    public async Task An_empty_ledger_deletes()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var svc = NewService(db);
        var l = await svc.CreateAsync(MainLedger("Scratch", "Nobody"));

        await svc.DeleteAsync(l.Id);
        Assert.Null(await svc.GetAsync(l.Id));
    }

    [SkippableFact]
    public async Task Transfers_and_entries_are_refused_on_a_closed_ledger()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        var b = await svc.GetAsync(bId);
        b!.Status = LedgerStatus.Settled;
        await svc.UpdateAsync(b);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddEntryAsync(
            new LedgerEntry
            {
                PlainLedgerId = bId, Direction = LedgerDirection.Out,
                Amount = 100, Description = "late entry"
            }, "u1", "Tester"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TransferAsync(
            mainId, bId, 100, new DateOnly(2026, 8, 1), "late transfer",
            null, LedgerPaymentMethod.Cash, "u1", "Tester"));
    }

    [SkippableFact]
    public async Task Nonsense_amounts_and_self_transfers_are_refused()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var svc = NewService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddEntryAsync(
            new LedgerEntry
            {
                PlainLedgerId = mainId, Direction = LedgerDirection.In,
                Amount = 0, Description = "zero"
            }, "u1", "Tester"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TransferAsync(
            mainId, mainId, 100, new DateOnly(2026, 8, 1), "to itself",
            null, LedgerPaymentMethod.Cash, "u1", "Tester"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TransferAsync(
            mainId, bId, -50, new DateOnly(2026, 8, 1), "negative",
            null, LedgerPaymentMethod.Cash, "u1", "Tester"));
    }

    [SkippableFact]
    public async Task Heads_nest_and_roll_child_totals_into_the_parent()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var heads = new LedgerHeadService(db);
        var svc = NewService(db);

        var expenses = await heads.SaveAsync(new LedgerHead { Name = "Expenses" });
        var rent = await heads.SaveAsync(new LedgerHead { Name = "Rent", ParentHeadId = expenses.Id });

        // One entry under the parent, one under the child.
        await svc.AddEntryAsync(new LedgerEntry
        {
            PlainLedgerId = mainId, Date = new DateOnly(2026, 7, 11),
            Direction = LedgerDirection.Out, Amount = 1_000,
            Description = "Sundry", HeadId = expenses.Id
        }, "u1", "Tester");
        await svc.AddEntryAsync(new LedgerEntry
        {
            PlainLedgerId = bId, Date = new DateOnly(2026, 7, 12),
            Direction = LedgerDirection.Out, Amount = 4_000,
            Description = "Office rent", HeadId = rent.Id
        }, "u1", "Tester");

        var totals = await heads.GetTotalsAsync();

        // The parent counts only its own entry, but its rollup includes the child's.
        Assert.Equal(1_000, totals[expenses.Id].OwnOut);
        Assert.Equal(5_000, totals[expenses.Id].RollupOut);
        Assert.Equal(4_000, totals[rent.Id].OwnOut);
        Assert.Equal(4_000, totals[rent.Id].RollupOut);
    }

    [SkippableFact]
    public async Task Deleting_a_head_leaves_the_money_and_only_drops_the_classification()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, _, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var heads = new LedgerHeadService(db);
        var svc = NewService(db);

        var head = await heads.SaveAsync(new LedgerHead { Name = "Misc" });
        var entry = await svc.AddEntryAsync(new LedgerEntry
        {
            PlainLedgerId = mainId, Date = new DateOnly(2026, 7, 15),
            Direction = LedgerDirection.Out, Amount = 700,
            Description = "Bits and pieces", HeadId = head.Id
        }, "u1", "Tester");

        await heads.DeleteAsync(head.Id);

        var after = await svc.GetEntryAsync(entry.Id);
        Assert.NotNull(after);
        Assert.Equal(700, after!.Amount);   // money untouched
        Assert.Null(after.HeadId);          // just unclassified now
    }

    [SkippableFact]
    public async Task A_head_with_sub_heads_refuses_deletion_and_cannot_sit_under_its_own_child()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var heads = new LedgerHeadService(db);

        var parent = await heads.SaveAsync(new LedgerHead { Name = "Top" });
        var child = await heads.SaveAsync(new LedgerHead { Name = "Under", ParentHeadId = parent.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() => heads.DeleteAsync(parent.Id));

        parent.ParentHeadId = child.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => heads.SaveAsync(parent));
    }

    [SkippableFact]
    public async Task A_transfer_puts_the_head_on_both_halves()
    {
        IntegrationDatabase.Require(_available);
        var (mainId, bId, _) = await SeedScenarioAsync();

        await using var db = NewDb();
        var heads = new LedgerHeadService(db);
        var svc = NewService(db);

        var head = await heads.SaveAsync(new LedgerHead { Name = "Advances Given" });
        await svc.TransferAsync(mainId, bId, 5_000, new DateOnly(2026, 7, 20),
            "Extra advance", null, LedgerPaymentMethod.Cash, "u1", "Tester", head.Id);

        var both = await db.Entries.AsNoTracking()
            .Where(e => e.Description == "Extra advance").ToListAsync();
        Assert.Equal(2, both.Count);
        Assert.All(both, e => Assert.Equal(head.Id, e.HeadId));
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
