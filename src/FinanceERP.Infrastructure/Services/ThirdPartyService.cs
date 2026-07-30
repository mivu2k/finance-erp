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

    public async Task<List<LedgerRowDto>> GetStatementAsync(
        int partyId, DateOnly? from = null, DateOnly? to = null)
    {
        var tp = await db.ThirdParties.AsNoTracking().FirstOrDefaultAsync(t => t.Id == partyId);
        if (tp?.AccountId is not { } accountId) return [];

        // Reuses the general ledger rather than querying voucher lines again, so the
        // party statement and the ledger page can never show different numbers.
        return await reports.GeneralLedgerAsync(new ReportFilter
        {
            AccountId = accountId, From = from, To = to, PageSize = int.MaxValue
        });
    }

    public async Task<decimal> GetBalanceAsync(int partyId)
    {
        var tp = await db.ThirdParties.AsNoTracking().FirstOrDefaultAsync(t => t.Id == partyId);
        return tp?.AccountId is { } accountId
            ? await accountService.GetBalanceAsync(accountId)
            : 0m;
    }
}
