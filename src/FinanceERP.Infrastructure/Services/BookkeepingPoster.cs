using ErpPlatform.Shared.Kernel;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

/// <summary>
/// Finance's implementation of the Kernel's <see cref="IBookkeepingPoster"/>: the
/// one doorway a business module has into the formal books.
/// </summary>
/// <remarks>
/// Everything still goes through <see cref="IVoucherService.PostSystemVoucherAsync"/>,
/// so the platform rule that no module writes financial state directly holds — this
/// only adapts a dependency-free signature onto it. Modules never see
/// <c>VoucherType</c> or the Finance domain.
/// </remarks>
public class BookkeepingPoster(IVoucherService vouchers, AppDbContext db) : IBookkeepingPoster
{
    public bool IsAvailable => true;

    public async Task<int?> PostAsync(
        DateOnly date, string narration, string source, int? sourceId,
        IEnumerable<BookkeepingLine> lines, CancellationToken ct = default)
    {
        var list = lines.ToList();
        if (list.Count == 0)
            throw new InvalidOperationException("A voucher needs at least one line.");
        if (list.Any(l => l.Debit < 0 || l.Credit < 0))
            throw new InvalidOperationException("Debits and credits can't be negative.");

        // Refuse an unbalanced journal here rather than letting it reach the books:
        // a module getting this wrong must not be able to break the trial balance.
        var debit = list.Sum(l => l.Debit);
        var credit = list.Sum(l => l.Credit);
        if (Math.Round(debit - credit, 2) != 0)
            throw new InvalidOperationException(
                $"Journal doesn't balance: debits {debit:N2} against credits {credit:N2}.");

        var voucher = await vouchers.PostSystemVoucherAsync(
            VoucherType.Journal, date, narration, source, sourceId,
            list.Select(l => (l.AccountId, l.Debit, l.Credit, l.Description)));

        return voucher.Id;
    }

    public async Task<IReadOnlyList<BookkeepingAccount>> ListAccountsAsync(CancellationToken ct = default) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.IsActive && a.IsPostable)
            .OrderBy(a => a.Code)
            .Select(a => new BookkeepingAccount(a.Id, a.Code, a.Name, a.Type.ToString()))
            .ToListAsync(ct);
}
