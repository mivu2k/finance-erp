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
        if (!_available) return;
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
    }

    /// <summary>
    /// Only the handful of accounts the party flow touches: the two parents it hangs
    /// parties under, and a cash account for the other side of each posting.
    /// </summary>
    private static async Task SeedMinimalChartAsync(AppDbContext db)
    {
        var assets = new Account { Code = "1000", Name = "Assets", Type = AccountType.Asset, IsPostable = false };
        var liabilities = new Account { Code = "2000", Name = "Liabilities", Type = AccountType.Liability, IsPostable = false };
        db.Accounts.AddRange(assets, liabilities);
        await db.SaveChangesAsync();

        db.Accounts.AddRange(
            new Account { Code = "1100", Name = "Cash in Hand", Type = AccountType.Asset, ParentId = assets.Id },
            new Account { Code = "1600", Name = "Receivables", Type = AccountType.Asset, ParentId = assets.Id, IsPostable = false },
            new Account { Code = "2100", Name = "Payables", Type = AccountType.Liability, ParentId = liabilities.Id, IsPostable = false });
        await db.SaveChangesAsync();
    }

    private async Task<int> CashAccountIdAsync()
    {
        await using var db = NewDb();
        return (await db.Accounts.AsNoTracking().FirstAsync(a => a.Code == "1100")).Id;
    }

    [Fact]
    public async Task A_receivable_party_gets_its_account_under_receivables()
    {
        if (!_available) return;

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

    [Fact]
    public async Task A_payable_party_gets_its_account_under_payables()
    {
        if (!_available) return;

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

    [Fact]
    public async Task Debit_pays_them_out_of_cash()
    {
        if (!_available) return;
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

    [Fact]
    public async Task Credit_takes_money_in_from_them()
    {
        if (!_available) return;
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

    [Fact]
    public async Task The_balance_and_statement_follow_the_postings()
    {
        if (!_available) return;
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
        Assert.Equal(30_000, statement.Last().RunningBalance);
    }

    [Fact]
    public async Task Nonsense_postings_are_refused()
    {
        if (!_available) return;
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

    [Fact]
    public async Task A_narration_defaults_to_the_party_and_direction_when_left_blank()
    {
        if (!_available) return;
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
}
