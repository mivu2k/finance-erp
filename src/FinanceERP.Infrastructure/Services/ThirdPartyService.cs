using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class ThirdPartyService(AppDbContext db, IAccountService accountService) : IThirdPartyService
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
            var parentCode = tp.Type is ThirdPartyType.Customer or ThirdPartyType.Borrower ? "1600" : "2100";
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

    public async Task DeleteAsync(int id)
    {
        var tp = await db.ThirdParties.FirstAsync(t => t.Id == id);
        db.ThirdParties.Remove(tp); // soft delete
        await db.SaveChangesAsync();
    }
}
