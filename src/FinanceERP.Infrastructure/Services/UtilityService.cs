using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class UtilityService(AppDbContext db, IVoucherService voucherService) : IUtilityService
{
    public Task<List<UtilityLocation>> GetLocationsAsync(bool includeConnections = false)
    {
        var q = db.UtilityLocations.AsNoTracking().AsQueryable();
        if (includeConnections)
            q = q.Include(l => l.Connections.Where(c => !c.IsDeleted)).ThenInclude(c => c.ExpenseAccount);
        return q.OrderBy(l => l.Name).ToListAsync();
    }

    public async Task<UtilityLocation> SaveLocationAsync(UtilityLocation location)
    {
        if (location.Id == 0)
        {
            db.UtilityLocations.Add(location);
        }
        else
        {
            var existing = await db.UtilityLocations.FirstAsync(l => l.Id == location.Id);
            existing.Name = location.Name;
            existing.IsActive = location.IsActive;
            location = existing;
        }
        await db.SaveChangesAsync();
        return location;
    }

    public async Task<UtilityConnection> SaveConnectionAsync(UtilityConnection connection)
    {
        if (connection.Id == 0)
        {
            db.UtilityConnections.Add(connection);
        }
        else
        {
            var existing = await db.UtilityConnections.FirstAsync(c => c.Id == connection.Id);
            existing.Name = connection.Name;
            existing.Type = connection.Type;
            existing.LocationId = connection.LocationId;
            existing.ConsumerNumber = connection.ConsumerNumber;
            existing.Provider = connection.Provider;
            existing.ExpenseAccountId = connection.ExpenseAccountId;
            existing.IsActive = connection.IsActive;
            connection = existing;
        }
        await db.SaveChangesAsync();
        return connection;
    }

    public async Task DeleteConnectionAsync(int id)
    {
        var connection = await db.UtilityConnections.FirstAsync(c => c.Id == id);
        if (await db.UtilityBills.AnyAsync(b => b.ConnectionId == id && b.VoucherId != null))
            throw new InvalidOperationException("Connection has paid bills; deactivate it instead.");
        db.UtilityConnections.Remove(connection); // soft delete
        await db.SaveChangesAsync();
    }

    private IQueryable<UtilityBill> Filtered(UtilityBillFilter f)
    {
        var q = db.UtilityBills.AsNoTracking()
            .Include(b => b.Connection).ThenInclude(c => c.Location)
            .AsQueryable();
        if (f.LocationId is not null) q = q.Where(b => b.Connection.LocationId == f.LocationId);
        if (f.ConnectionId is not null) q = q.Where(b => b.ConnectionId == f.ConnectionId);
        if (f.Type is not null) q = q.Where(b => b.Connection.Type == f.Type);
        if (f.From is not null) q = q.Where(b => b.BillMonth >= f.From);
        if (f.To is not null) q = q.Where(b => b.BillMonth <= f.To);
        if (f.Paid is not null) q = q.Where(b => (b.VoucherId != null) == f.Paid);
        return q;
    }

    public Task<List<UtilityBill>> ListBillsAsync(UtilityBillFilter filter, int max = 500) =>
        Filtered(filter)
            .OrderByDescending(b => b.BillMonth).ThenBy(b => b.Connection.Location.Name).ThenBy(b => b.Connection.Name)
            .Take(max).ToListAsync();

    public async Task<UtilityBill> AddBillAsync(UtilityBill bill)
    {
        if (bill.Amount <= 0) throw new InvalidOperationException("Bill amount must be positive.");
        bill.BillMonth = new DateOnly(bill.BillMonth.Year, bill.BillMonth.Month, 1);
        if (await db.UtilityBills.AnyAsync(b => b.ConnectionId == bill.ConnectionId && b.BillMonth == bill.BillMonth))
            throw new InvalidOperationException("A bill for this connection and month already exists.");
        db.UtilityBills.Add(bill);
        await db.SaveChangesAsync();
        return bill;
    }

    public async Task DeleteBillAsync(int id)
    {
        var bill = await db.UtilityBills.FirstAsync(b => b.Id == id);
        if (bill.VoucherId is not null) throw new InvalidOperationException("Paid bills cannot be deleted.");
        db.UtilityBills.Remove(bill); // soft delete
        await db.SaveChangesAsync();
    }

    public async Task<Voucher> PayBillAsync(int billId, int payFromAccountId, DateOnly? paidDate = null)
    {
        var bill = await db.UtilityBills
            .Include(b => b.Connection).ThenInclude(c => c.Location)
            .FirstAsync(b => b.Id == billId);
        if (bill.VoucherId is not null) throw new InvalidOperationException("Bill is already paid.");

        var expenseAccountId = bill.Connection.ExpenseAccountId ?? await DefaultExpenseAccountAsync(bill.Connection.Type);
        var date = paidDate ?? DateOnly.FromDateTime(DateTime.Today);
        var label = $"{bill.Connection.Location.Name} / {bill.Connection.Name}" +
                    (bill.Connection.ConsumerNumber is null ? "" : $" ({bill.Connection.ConsumerNumber})");

        var voucher = await voucherService.PostSystemVoucherAsync(
            VoucherType.CashPayment, date,
            $"Utility bill {bill.BillMonth:MMM yyyy} — {label}",
            "UtilityBill", bill.Id,
            [
                (expenseAccountId, bill.Amount, 0m, label),
                (payFromAccountId, 0m, bill.Amount, $"Utility bill {bill.BillMonth:MMM yyyy} — {label}")
            ]);

        bill.VoucherId = voucher.Id;
        bill.PaidDate = date;
        await db.SaveChangesAsync();
        return voucher;
    }

    private async Task<int> DefaultExpenseAccountAsync(UtilityType type)
    {
        var code = type switch
        {
            UtilityType.Internet or UtilityType.Telephone => "5130", // Internet
            _ => "5120" // Utilities
        };
        return (await db.Accounts.FirstAsync(a => a.Code == code)).Id;
    }

    public async Task<List<ExpenseBreakdownDto>> SummaryByTypeAsync(UtilityBillFilter filter)
    {
        var rows = await Filtered(filter)
            .GroupBy(b => b.Connection.Type)
            .Select(g => new { g.Key, Amount = g.Sum(x => x.Amount) })
            .ToListAsync();
        return rows.OrderByDescending(r => r.Amount)
            .Select(r => new ExpenseBreakdownDto(r.Key.ToString(), r.Amount)).ToList();
    }

    public async Task<List<ExpenseBreakdownDto>> SummaryByLocationAsync(UtilityBillFilter filter)
    {
        var rows = await Filtered(filter)
            .GroupBy(b => b.Connection.Location.Name)
            .Select(g => new { g.Key, Amount = g.Sum(x => x.Amount) })
            .ToListAsync();
        return rows.OrderByDescending(r => r.Amount)
            .Select(r => new ExpenseBreakdownDto(r.Key, r.Amount)).ToList();
    }
}
