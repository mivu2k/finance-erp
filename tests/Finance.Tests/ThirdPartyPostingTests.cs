using ErpPlatform.TestSupport;
using ErpPlatform.Shared.Kernel;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using FinanceERP.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Finance.Tests;

/// <summary>
/// The party screen is a shortcut to a journal, so the thing worth pinning down is
/// that the shortcut lands the debits and credits on the right sides. Getting this
/// backwards would quietly invert a balance sheet, and nothing else would complain.
/// </summary>
public class ThirdPartyPostingTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"finance_erp_test_{Guid.NewGuid():N}"[..30];
    private bool _available;

    private DbContextOptions<AppDbContext> Opts() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

    private AppDbContext NewDb() => new(Opts(), new TestUser());

    private static (IThirdPartyService Parties, IAccountService Accounts) Services(AppDbContext db)
    {
        var accounts = new AccountService(db);
        var vouchers = new VoucherService(db, new TestUser());
        var reports = new ReportService(db);
        return (new ThirdPartyService(db, accounts, vouchers, reports), accounts);
    }

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try
        {
            await db.Database.EnsureCreatedAsync();
            await SeedMinimalChartAsync(db);
            _available = true;
        }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;   // nothing was created, so nothing to drop
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
    }

    /// <summary>
    /// Only the handful of accounts the party flow touches: the two parents it hangs
    /// parties under, and two cash heads — two, so a test can tell "the head you used
    /// last time" apart from "the first head in the list".
    /// </summary>
    private static async Task SeedMinimalChartAsync(AppDbContext db)
    {
        var assets = new Account { Code = "1000", Name = "Assets", Type = AccountType.Asset, IsPostable = false };
        var liabilities = new Account { Code = "2000", Name = "Liabilities", Type = AccountType.Liability, IsPostable = false };
        db.Accounts.AddRange(assets, liabilities);
        await db.SaveChangesAsync();

        db.Accounts.AddRange(
            new Account { Code = "1100", Name = "Cash in Hand", Type = AccountType.Asset, ParentId = assets.Id },
            new Account { Code = "1300", Name = "Petty Cash", Type = AccountType.Asset, ParentId = assets.Id },
            new Account { Code = "1600", Name = "Receivables", Type = AccountType.Asset, ParentId = assets.Id, IsPostable = false },
            new Account { Code = "2100", Name = "Payables", Type = AccountType.Liability, ParentId = liabilities.Id, IsPostable = false });
        await db.SaveChangesAsync();
    }

    private async Task<int> CashAccountIdAsync()
    {
        await using var db = NewDb();
        return (await db.Accounts.AsNoTracking().FirstAsync(a => a.Code == "1100")).Id;
    }

    [SkippableFact]
    public async Task A_receivable_party_gets_its_account_under_receivables()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var (parties, _) = Services(db);

        var tp = await parties.SaveAsync(new ThirdParty
        {
            Name = "Mr A", Type = ThirdPartyType.Receivable
        });

        Assert.NotNull(tp.AccountId);
        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == tp.AccountId);
        var parent = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == account.ParentId);
        Assert.Equal("1600", parent.Code);
    }

    [SkippableFact]
    public async Task A_payable_party_gets_its_account_under_payables()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var (parties, _) = Services(db);

        var tp = await parties.SaveAsync(new ThirdParty
        {
            Name = "Karachi Supplies", Type = ThirdPartyType.Payable
        });

        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == tp.AccountId);
        var parent = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == account.ParentId);
        Assert.Equal("2100", parent.Code);
    }

    [SkippableFact]
    public async Task Debit_pays_them_out_of_cash()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);
        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr A", Type = ThirdPartyType.Receivable });

        var voucher = await parties.RecordAsync(
            tp.Id, PartyMovement.Debit, 50_000, cashId, new DateOnly(2026, 7, 30), "Advance to Mr A");

        var lines = await db.VoucherLines.AsNoTracking()
            .Where(l => l.VoucherId == voucher.Id).ToListAsync();

        Assert.Equal(2, lines.Count);
        // Money out: their account owes more, cash goes down.
        Assert.Equal(50_000, lines.Single(l => l.AccountId == tp.AccountId).Debit);
        Assert.Equal(50_000, lines.Single(l => l.AccountId == cashId).Credit);
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    [SkippableFact]
    public async Task Credit_takes_money_in_from_them()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);
        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr A", Type = ThirdPartyType.Receivable });

        var voucher = await parties.RecordAsync(
            tp.Id, PartyMovement.Credit, 20_000, cashId, new DateOnly(2026, 7, 31), "Repaid by Mr A");

        var lines = await db.VoucherLines.AsNoTracking()
            .Where(l => l.VoucherId == voucher.Id).ToListAsync();

        // Money in: cash goes up, what they owe comes down.
        Assert.Equal(20_000, lines.Single(l => l.AccountId == cashId).Debit);
        Assert.Equal(20_000, lines.Single(l => l.AccountId == tp.AccountId).Credit);
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    [SkippableFact]
    public async Task The_balance_and_statement_follow_the_postings()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);
        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr A", Type = ThirdPartyType.Receivable });

        await parties.RecordAsync(tp.Id, PartyMovement.Debit, 50_000, cashId,
            new DateOnly(2026, 7, 30), "Advance");
        await parties.RecordAsync(tp.Id, PartyMovement.Credit, 20_000, cashId,
            new DateOnly(2026, 7, 31), "Part repaid");

        // 50,000 out then 20,000 back: they are still holding 30,000.
        Assert.Equal(30_000, await parties.GetBalanceAsync(tp.Id));

        var statement = await parties.GetStatementAsync(tp.Id);
        Assert.Equal(2, statement.Count);
        Assert.Equal(30_000, statement[^1].Balance);
    }

    [SkippableFact]
    public async Task Nonsense_postings_are_refused()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);
        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr A", Type = ThirdPartyType.Receivable });

        await Assert.ThrowsAsync<InvalidOperationException>(() => parties.RecordAsync(
            tp.Id, PartyMovement.Debit, 0, cashId, new DateOnly(2026, 7, 30)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => parties.RecordAsync(
            tp.Id, PartyMovement.Debit, -5, cashId, new DateOnly(2026, 7, 30)));

        // Posting a party against their own account would be a meaningless self-entry.
        await Assert.ThrowsAsync<InvalidOperationException>(() => parties.RecordAsync(
            tp.Id, PartyMovement.Debit, 100, tp.AccountId!.Value, new DateOnly(2026, 7, 30)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => parties.RecordAsync(
            9999, PartyMovement.Debit, 100, cashId, new DateOnly(2026, 7, 30)));
    }

    [SkippableFact]
    public async Task A_narration_defaults_to_the_party_and_direction_when_left_blank()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);
        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr A", Type = ThirdPartyType.Receivable });

        var paid = await parties.RecordAsync(tp.Id, PartyMovement.Debit, 10, cashId, new DateOnly(2026, 7, 30));
        var got = await parties.RecordAsync(tp.Id, PartyMovement.Credit, 10, cashId, new DateOnly(2026, 7, 30));

        Assert.Contains("Paid to Mr A", paid.Narration);
        Assert.Contains("Received from Mr A", got.Narration);
    }

    private sealed class TestUser : ICurrentUserService
    {
        public string? UserId => "test";
        public string? UserName => "test";
        public string? IpAddress => null;
        public string? Browser => null;
        public bool HasPermission(string permission) => true;
    }

    [SkippableFact]
    public async Task The_statement_names_the_head_the_money_moved_through()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);
        var cash = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == cashId);

        // The day-to-day case: they hand us money, we put it in cash, we pay it back later.
        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr B", Type = ThirdPartyType.Payable });
        await parties.RecordAsync(tp.Id, PartyMovement.Credit, 100_000, cashId,
            new DateOnly(2026, 7, 1), "Received from Mr B");
        await parties.RecordAsync(tp.Id, PartyMovement.Debit, 30_000, cashId,
            new DateOnly(2026, 7, 20), "Part payment to Mr B");

        var rows = await parties.GetStatementAsync(tp.Id);

        Assert.Equal(2, rows.Count);
        // The column that matters: which head, not the party's own account name.
        Assert.All(rows, r => Assert.Equal(cash.Code, r.ContraCode));
        Assert.All(rows, r => Assert.Equal(cash.Name, r.ContraName));

        Assert.Equal(100_000, rows[0].Received);
        Assert.Equal(0, rows[0].Paid);
        Assert.Equal(30_000, rows[1].Paid);
        Assert.Equal(0, rows[1].Received);
    }

    [SkippableFact]
    public async Task The_running_balance_reads_as_what_is_still_outstanding()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);

        // Payable: took 100,000, paid back 30,000 — we still owe 70,000.
        var payable = await parties.SaveAsync(new ThirdParty { Name = "Mr B", Type = ThirdPartyType.Payable });
        await parties.RecordAsync(payable.Id, PartyMovement.Credit, 100_000, cashId, new DateOnly(2026, 7, 1));
        await parties.RecordAsync(payable.Id, PartyMovement.Debit, 30_000, cashId, new DateOnly(2026, 7, 20));

        var owed = await parties.GetStatementAsync(payable.Id);
        Assert.Equal(100_000, owed[0].Balance);
        Assert.Equal(70_000, owed[^1].Balance);

        // Receivable is the mirror: gave 50,000, got 20,000 back — they still owe 30,000.
        var receivable = await parties.SaveAsync(new ThirdParty { Name = "Mr A", Type = ThirdPartyType.Receivable });
        await parties.RecordAsync(receivable.Id, PartyMovement.Debit, 50_000, cashId, new DateOnly(2026, 7, 1));
        await parties.RecordAsync(receivable.Id, PartyMovement.Credit, 20_000, cashId, new DateOnly(2026, 7, 20));

        var due = await parties.GetStatementAsync(receivable.Id);
        Assert.Equal(30_000, due[^1].Balance);
    }

    [SkippableFact]
    public async Task Head_totals_add_up_across_every_party()
    {
        IntegrationDatabase.Require(_available);
        var cashId = await CashAccountIdAsync();

        await using var db = NewDb();
        var (parties, _) = Services(db);
        var cash = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == cashId);

        var a = await parties.SaveAsync(new ThirdParty { Name = "Mr A", Type = ThirdPartyType.Payable });
        var b = await parties.SaveAsync(new ThirdParty { Name = "Mr B", Type = ThirdPartyType.Payable });

        await parties.RecordAsync(a.Id, PartyMovement.Credit, 100_000, cashId, new DateOnly(2026, 7, 1));
        await parties.RecordAsync(b.Id, PartyMovement.Credit, 40_000, cashId, new DateOnly(2026, 7, 2));
        await parties.RecordAsync(a.Id, PartyMovement.Debit, 25_000, cashId, new DateOnly(2026, 7, 20));

        var totals = await parties.GetHeadTotalsAsync();
        var row = Assert.Single(totals, t => t.Code == cash.Code);

        // Money received from parties landed in cash (debit); money paid left it (credit).
        Assert.Equal(140_000, row.Received);
        Assert.Equal(25_000, row.Paid);
        Assert.Equal(115_000, row.Net);
        Assert.Equal(3, row.Movements);
    }

    [SkippableFact]
    public async Task The_last_head_used_is_remembered_for_next_time()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var (parties, _) = Services(db);

        // Two different heads, settled through the second one most recently.
        var cash = await db.Accounts.AsNoTracking().FirstAsync(a => a.Code == "1100");
        var petty = await db.Accounts.AsNoTracking().FirstAsync(a => a.Code == "1300");

        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr C", Type = ThirdPartyType.Payable });
        Assert.Null(await parties.GetLastHeadAsync(tp.Id));   // nothing posted yet

        await parties.RecordAsync(tp.Id, PartyMovement.Credit, 10_000, cash.Id, new DateOnly(2026, 7, 1));
        await parties.RecordAsync(tp.Id, PartyMovement.Debit, 2_000, petty.Id, new DateOnly(2026, 7, 20));

        Assert.Equal(petty.Id, await parties.GetLastHeadAsync(tp.Id));
    }

    [SkippableFact]
    public async Task A_party_with_no_account_has_no_remembered_head()
    {
        IntegrationDatabase.Require(_available);

        await using var db = NewDb();
        var (parties, _) = Services(db);

        // Nothing blows up on a party that was never posted against.
        var tp = await parties.SaveAsync(new ThirdParty { Name = "Mr D", Type = ThirdPartyType.Receivable });
        Assert.Null(await parties.GetLastHeadAsync(tp.Id));
        Assert.Empty(await parties.GetStatementAsync(tp.Id));
    }
}
