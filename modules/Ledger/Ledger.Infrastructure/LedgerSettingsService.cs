using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure;

/// <summary>
/// The module's own small settings table. Kept here rather than in Finance's
/// settings because it configures how <em>this</em> module posts, and the module
/// has to keep working with accounting absent.
/// </summary>
public interface ILedgerSettingsService
{
    Task<int?> GetCashAccountIdAsync(CancellationToken ct = default);
    Task SetCashAccountIdAsync(int? accountId, CancellationToken ct = default);

    /// <summary>Accounts a ledger may be mapped to. Empty when accounting isn't wired in.</summary>
    Task<IReadOnlyList<BookkeepingAccount>> ListAccountsAsync(CancellationToken ct = default);
    bool PostingAvailable { get; }
}

public class LedgerSettingsService(LedgerDbContext db, IBookkeepingPoster poster) : ILedgerSettingsService
{
    public bool PostingAvailable => poster.IsAvailable;

    public async Task<int?> GetCashAccountIdAsync(CancellationToken ct = default)
    {
        var raw = await db.Settings.AsNoTracking()
            .Where(s => s.Key == LedgerSettingKeys.CashAccountId)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var id) ? id : null;
    }

    public async Task SetCashAccountIdAsync(int? accountId, CancellationToken ct = default)
    {
        var row = await db.Settings.FirstOrDefaultAsync(
            s => s.Key == LedgerSettingKeys.CashAccountId, ct);

        if (row is null)
        {
            db.Settings.Add(new LedgerSetting
            {
                Key = LedgerSettingKeys.CashAccountId,
                Value = accountId?.ToString()
            });
        }
        else
        {
            row.Value = accountId?.ToString();
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<BookkeepingAccount>> ListAccountsAsync(CancellationToken ct = default) =>
        poster.ListAccountsAsync(ct);
}
