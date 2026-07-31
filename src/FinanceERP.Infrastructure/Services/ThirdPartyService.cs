using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class ThirdPartyService(
    AppDbContext db,
    IAccountService accountService,
    IVoucherService vouchers,
    IReportService reports) : IThirdPartyService
{
    public async Task<PagedResult<ThirdParty>> ListAsync(ReportFilter f)
    {
        var q = db.ThirdParties.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(f.Search))
            q = q.Where(t => t.Name.Contains(f.Search) || (t.Phone != null && t.Phone.Contains(f.Search)));
        var total = await q.CountAsync();
        var items = await q.OrderBy(t => t.Name)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        return new PagedResult<ThirdParty>(items, total);
    }

    public async Task<ThirdParty> SaveAsync(ThirdParty tp)
    {
        if (tp.Id == 0)
        {
            db.ThirdParties.Add(tp);
            await db.SaveChangesAsync();
            // Receivable-type parties sit under Receivables (1600), the rest under Payables (2100).
            var parentCode = tp.Type is ThirdPartyType.Receivable ? "1600" : "2100";
            var account = await accountService.EnsureChildAccountAsync(parentCode, tp.Name);
            tp.AccountId = account.Id;
        }
        else
        {
            var existing = await db.ThirdParties.FirstAsync(t => t.Id == tp.Id);
            existing.Name = tp.Name; existing.Type = tp.Type; existing.Phone = tp.Phone;
            existing.Email = tp.Email; existing.Address = tp.Address; existing.TaxNumber = tp.TaxNumber;
            existing.Notes = tp.Notes; existing.IsActive = tp.IsActive;
            tp = existing;
        }
        await db.SaveChangesAsync();
        return tp;
    }

    public Task<ThirdParty?> GetAsync(int id) =>
        db.ThirdParties.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

    public async Task DeleteAsync(int id)
    {
        var tp = await db.ThirdParties.FirstAsync(t => t.Id == id);
        db.ThirdParties.Remove(tp); // soft delete
        await db.SaveChangesAsync();
    }

    public async Task<Voucher> RecordAsync(
        int partyId, PartyMovement movement, decimal amount,
        int cashAccountId, DateOnly date, string? narration = null)
    {
        if (amount <= 0)
            throw new InvalidOperationException("Amount must be positive.");

        var tp = await db.ThirdParties.AsNoTracking().FirstOrDefaultAsync(t => t.Id == partyId)
                 ?? throw new InvalidOperationException("Party not found.");

        // A party saved before its account was created has nothing to post against.
        var partyAccountId = tp.AccountId
            ?? throw new InvalidOperationException(
                $"{tp.Name} has no ledger account yet. Re-save the party to create one.");

        if (cashAccountId == partyAccountId)
            throw new InvalidOperationException("Pick a different cash or bank account.");

        var text = string.IsNullOrWhiteSpace(narration)
            ? $"{(movement == PartyMovement.Debit ? "Paid to" : "Received from")} {tp.Name}"
            : narration.Trim();

        // Everything still goes through the voucher service — the party screen is a
        // shortcut to a journal, not a second place financial state is written.
        var lines = movement == PartyMovement.Debit
            ? new[]
            {
                (partyAccountId, amount, 0m, (string?)text),
                (cashAccountId, 0m, amount, (string?)text)
            }
            : [
                (cashAccountId, amount, 0m, (string?)text),
                (partyAccountId, 0m, amount, (string?)text)
            ];

        return await vouchers.PostSystemVoucherAsync(
            VoucherType.Journal, date, text, "thirdparty", tp.Id, lines);
    }

    public async Task<List<PartyStatementRowDto>> GetStatementAsync(
        int partyId, DateOnly? from = null, DateOnly? to = null)
    {
        var tp = await db.ThirdParties.AsNoTracking().FirstOrDefaultAsync(t => t.Id == partyId);
        if (tp?.AccountId is not { } accountId) return [];

        // The party's own lines, posted only — a draft voucher is not money yet.
        var q = db.VoucherLines.AsNoTracking()
            .Where(l => l.AccountId == accountId
                        && l.Voucher.Status == VoucherStatus.Posted
                        && !l.Voucher.IsDeleted);

        if (from is { } f) q = q.Where(l => l.Voucher.Date >= f);
        if (to is { } t2) q = q.Where(l => l.Voucher.Date <= t2);

        var mine = await q
            .Select(l => new
            {
                l.VoucherId,
                l.Voucher.VoucherNo,
                l.Voucher.Date,
                l.Description,
                l.Debit,
                l.Credit
            })
            .OrderBy(x => x.Date).ThenBy(x => x.VoucherId)
            .ToListAsync();

        if (mine.Count == 0) return [];

        // One extra query for the other side of those vouchers, rather than one per row.
        var voucherIds = mine.Select(x => x.VoucherId).Distinct().ToList();
        var others = await db.VoucherLines.AsNoTracking()
            .Where(l => voucherIds.Contains(l.VoucherId) && l.AccountId != accountId)
            .Select(l => new { l.VoucherId, l.AccountId, l.Account.Code, l.Account.Name, l.Debit, l.Credit })
            .ToListAsync();

        var contra = others.GroupBy(o => o.VoucherId).ToDictionary(
            g => g.Key,
            g =>
            {
                var distinct = g.GroupBy(x => x.Code).ToList();
                // A simple two-line entry names its head; a split names how many, because
                // inventing one head for a multi-line voucher would be a lie.
                return distinct.Count == 1
                    ? (Code: (string?)distinct[0].Key, Name: (string?)distinct[0].First().Name,
                       Id: (int?)distinct[0].First().AccountId)
                    : (Code: null, Name: (string?)$"Split — {distinct.Count} heads", Id: (int?)null);
            });

        // The party's side, signed so the running figure reads as what is outstanding:
        // a Receivable grows when we debit them, a Payable when we credit them.
        var receivableSide = tp.Type == ThirdPartyType.Receivable;

        decimal running = 0;
        var rows = new List<PartyStatementRowDto>(mine.Count);
        foreach (var m in mine)
        {
            running += receivableSide ? m.Debit - m.Credit : m.Credit - m.Debit;
            contra.TryGetValue(m.VoucherId, out var head);

            rows.Add(new PartyStatementRowDto(
                m.Date, m.VoucherNo, m.VoucherId, m.Description,
                head.Code, head.Name, head.Id,
                Received: m.Credit,
                Paid: m.Debit,
                Balance: running));
        }

        return rows;
    }

    public async Task<int?> GetLastHeadAsync(int partyId)
    {
        var tp = await db.ThirdParties.AsNoTracking().FirstOrDefaultAsync(t => t.Id == partyId);
        if (tp?.AccountId is not { } accountId) return null;

        // The newest posted voucher that touched this party, then its other side.
        var lastVoucherId = await db.VoucherLines.AsNoTracking()
            .Where(l => l.AccountId == accountId
                        && l.Voucher.Status == VoucherStatus.Posted && !l.Voucher.IsDeleted)
            .OrderByDescending(l => l.Voucher.Date).ThenByDescending(l => l.VoucherId)
            .Select(l => (int?)l.VoucherId)
            .FirstOrDefaultAsync();

        if (lastVoucherId is not { } vid) return null;

        // Only when it was a clean two-sided entry; a split has no single head to reuse.
        var otherIds = await db.VoucherLines.AsNoTracking()
            .Where(l => l.VoucherId == vid && l.AccountId != accountId)
            .Select(l => l.AccountId).Distinct().ToListAsync();

        return otherIds.Count == 1 ? otherIds[0] : null;
    }

    public async Task<List<PartyHeadTotalDto>> GetHeadTotalsAsync(
        DateOnly? from = null, DateOnly? to = null)
    {
        var partyAccountIds = await db.ThirdParties.AsNoTracking()
            .Where(t => t.AccountId != null)
            .Select(t => t.AccountId!.Value)
            .ToListAsync();

        if (partyAccountIds.Count == 0) return [];

        // Vouchers that touched any party account at all.
        var vq = db.VoucherLines.AsNoTracking()
            .Where(l => partyAccountIds.Contains(l.AccountId)
                        && l.Voucher.Status == VoucherStatus.Posted
                        && !l.Voucher.IsDeleted);

        if (from is { } f) vq = vq.Where(l => l.Voucher.Date >= f);
        if (to is { } t2) vq = vq.Where(l => l.Voucher.Date <= t2);

        var voucherIds = await vq.Select(l => l.VoucherId).Distinct().ToListAsync();
        if (voucherIds.Count == 0) return [];

        // The contra side of those vouchers, grouped by head. Debit on the head means
        // money landed there (received); credit means it left (paid out).
        var lines = await db.VoucherLines.AsNoTracking()
            .Where(l => voucherIds.Contains(l.VoucherId) && !partyAccountIds.Contains(l.AccountId))
            .Select(l => new { l.Account.Code, l.Account.Name, l.Debit, l.Credit })
            .ToListAsync();

        return lines
            .GroupBy(l => new { l.Code, l.Name })
            .Select(g => new PartyHeadTotalDto(
                g.Key.Code, g.Key.Name,
                Received: g.Sum(x => x.Debit),
                Paid: g.Sum(x => x.Credit),
                Movements: g.Count()))
            .OrderByDescending(h => h.Received + h.Paid)
            .ToList();
    }

    public async Task<decimal> GetBalanceAsync(int partyId)
    {
        var tp = await db.ThirdParties.AsNoTracking().FirstOrDefaultAsync(t => t.Id == partyId);
        return tp?.AccountId is { } accountId
            ? await accountService.GetBalanceAsync(accountId)
            : 0m;
    }
}
