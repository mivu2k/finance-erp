using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Infrastructure.Services;

public class VoucherService(AppDbContext db, ICurrentUserService currentUser) : IVoucherService
{
    private static readonly Dictionary<VoucherType, string> Prefixes = new()
    {
        [VoucherType.CashPayment] = "CPV",
        [VoucherType.CashReceipt] = "CRV",
        [VoucherType.BankPayment] = "BPV",
        [VoucherType.BankReceipt] = "BRV",
        [VoucherType.Journal] = "JV",
        [VoucherType.Adjustment] = "ADJ"
    };

    public async Task<PagedResult<VoucherListItemDto>> ListAsync(ReportFilter f)
    {
        var q = db.Vouchers.AsNoTracking();
        if (f.From is not null) q = q.Where(v => v.Date >= f.From);
        if (f.To is not null) q = q.Where(v => v.Date <= f.To);
        if (f.VoucherType is not null) q = q.Where(v => v.Type == f.VoucherType);
        if (f.VoucherStatus is not null) q = q.Where(v => v.Status == f.VoucherStatus);
        if (!string.IsNullOrWhiteSpace(f.Search))
            q = q.Where(v => v.VoucherNo.Contains(f.Search) || (v.Narration != null && v.Narration.Contains(f.Search)));

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(v => v.Date).ThenByDescending(v => v.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize)
            .Select(v => new VoucherListItemDto(v.Id, v.VoucherNo, v.Type, v.Status, v.Date,
                v.Narration, v.TotalDebit, v.Source, v.CreatedBy))
            .ToListAsync();
        return new PagedResult<VoucherListItemDto>(items, total);
    }

    public Task<Voucher?> GetAsync(int id) =>
        db.Vouchers.Include(v => v.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(v => v.Id == id);

    private async Task EnsureNotLockedAsync(DateOnly date)
    {
        var setting = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.BooksLockDate);
        if (setting is not null && DateOnly.TryParse(setting.Value, out var lockDate) && date <= lockDate)
            throw new InvalidOperationException($"Books are locked through {lockDate:yyyy-MM-dd}. Use a later voucher date.");
    }

    public async Task<Voucher> SaveAsync(VoucherEditDto dto, bool post)
    {
        await EnsureNotLockedAsync(dto.Date);
        var lines = dto.Lines.Where(l => l.AccountId is not null && (l.Debit != 0 || l.Credit != 0)).ToList();
        ValidateLines(lines);

        Voucher voucher;
        if (dto.Id == 0)
        {
            voucher = new Voucher
            {
                Type = dto.Type,
                Date = dto.Date,
                Narration = dto.Narration,
                VoucherNo = await NextNumberAsync(dto.Type, dto.Date.Year)
            };
            db.Vouchers.Add(voucher);
        }
        else
        {
            voucher = await db.Vouchers.Include(v => v.Lines).FirstAsync(v => v.Id == dto.Id);
            if (voucher.Status == VoucherStatus.Posted)
                throw new InvalidOperationException("Posted vouchers cannot be edited. Void it and create a new one.");
            voucher.Date = dto.Date;
            voucher.Narration = dto.Narration;
            db.VoucherLines.RemoveRange(voucher.Lines);
            voucher.Lines.Clear();
        }

        var lineNo = 1;
        foreach (var l in lines)
        {
            voucher.Lines.Add(new VoucherLine
            {
                AccountId = l.AccountId!.Value,
                Description = l.Description,
                Debit = l.Debit,
                Credit = l.Credit,
                CostCenterId = l.CostCenterId,
                DepartmentId = l.DepartmentId,
                ProjectId = l.ProjectId,
                ThirdPartyId = l.ThirdPartyId,
                AttachmentPath = l.AttachmentPath,
                AttachmentName = l.AttachmentName,
                LineNo = lineNo++
            });
        }
        voucher.TotalDebit = lines.Sum(l => l.Debit);
        voucher.TotalCredit = lines.Sum(l => l.Credit);

        if (post)
        {
            voucher.Status = VoucherStatus.Posted;
            voucher.PostedBy = currentUser.UserName;
            voucher.PostedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return voucher;
    }

    public async Task PostAsync(int id)
    {
        var voucher = await db.Vouchers.Include(v => v.Lines).FirstAsync(v => v.Id == id);
        await EnsureNotLockedAsync(voucher.Date);
        if (voucher.Status != VoucherStatus.Draft) throw new InvalidOperationException("Only draft vouchers can be posted.");
        if (voucher.TotalDebit != voucher.TotalCredit || voucher.TotalDebit == 0)
            throw new InvalidOperationException("Voucher is not balanced.");
        voucher.Status = VoucherStatus.Posted;
        voucher.PostedBy = currentUser.UserName;
        voucher.PostedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task VoidAsync(int id, string reason)
    {
        var voucher = await db.Vouchers.FirstAsync(v => v.Id == id);
        await EnsureNotLockedAsync(voucher.Date);
        voucher.Status = VoucherStatus.Void;
        voucher.Narration = $"{voucher.Narration} [VOID: {reason}]";
        await db.SaveChangesAsync();
    }

    public async Task<Voucher> PostSystemVoucherAsync(VoucherType type, DateOnly date, string narration,
        string source, int? sourceId,
        IEnumerable<(int AccountId, decimal Debit, decimal Credit, string? Description)> lines)
    {
        await EnsureNotLockedAsync(date);
        var lineList = lines.ToList();
        var totalD = lineList.Sum(l => l.Debit);
        var totalC = lineList.Sum(l => l.Credit);
        if (totalD != totalC || totalD == 0)
            throw new InvalidOperationException($"System voucher not balanced (D {totalD} / C {totalC}).");

        var voucher = new Voucher
        {
            Type = type,
            Date = date,
            Narration = narration,
            Source = source,
            SourceId = sourceId,
            Status = VoucherStatus.Posted,
            PostedBy = currentUser.UserName ?? "system",
            PostedAtUtc = DateTime.UtcNow,
            VoucherNo = await NextNumberAsync(type, date.Year),
            TotalDebit = totalD,
            TotalCredit = totalC,
            Lines = lineList.Select((l, i) => new VoucherLine
            {
                AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit,
                Description = l.Description, LineNo = i + 1
            }).ToList()
        };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();
        return voucher;
    }

    public async Task<Voucher> CloseFiscalYearAsync(DateOnly closeDate)
    {
        var retained = await db.Accounts.FirstAsync(a => a.Code == "3900");
        var balances = await db.VoucherLines.AsNoTracking()
            .Where(l => l.Voucher.Status == VoucherStatus.Posted && l.Voucher.Date <= closeDate
                && (l.Account.Type == AccountType.Income || l.Account.Type == AccountType.Expense))
            .GroupBy(l => new { l.AccountId, l.Account.Name })
            .Select(g => new { g.Key.AccountId, g.Key.Name, Net = g.Sum(x => x.Debit) - g.Sum(x => x.Credit) })
            .Where(x => x.Net != 0)
            .ToListAsync();
        if (balances.Count == 0)
            throw new InvalidOperationException("Nothing to close — no income/expense balances up to that date.");

        // Reverse every P&L balance; the offset lands in Retained Earnings.
        var lines = balances
            .Select(b => (b.AccountId,
                Debit: b.Net < 0 ? -b.Net : 0m,
                Credit: b.Net > 0 ? b.Net : 0m,
                Description: (string?)$"Year-end close — {b.Name}"))
            .ToList();
        var profit = balances.Sum(b => -b.Net); // credit-positive = profit
        lines.Add((retained.Id, profit < 0 ? -profit : 0m, profit > 0 ? profit : 0m,
            $"Year-end close — net {(profit >= 0 ? "profit" : "loss")} {Math.Abs(profit):N2}"));

        var voucher = await PostSystemVoucherAsync(VoucherType.Journal, closeDate,
            $"Fiscal year close as of {closeDate:yyyy-MM-dd}", "YearEnd", null, lines);

        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.BooksLockDate);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = SettingKeys.BooksLockDate, Value = closeDate.ToString("yyyy-MM-dd") });
        else setting.Value = closeDate.ToString("yyyy-MM-dd");
        await db.SaveChangesAsync();
        return voucher;
    }

    private static void ValidateLines(List<VoucherLineEditDto> lines)
    {
        if (lines.Count == 0) throw new InvalidOperationException("Voucher must have at least one line.");
        if (lines.Any(l => l.Debit < 0 || l.Credit < 0)) throw new InvalidOperationException("Amounts cannot be negative.");
        if (lines.Any(l => l.Debit > 0 && l.Credit > 0)) throw new InvalidOperationException("A line cannot have both debit and credit.");
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
            throw new InvalidOperationException("Debits and credits must balance.");
    }

    private async Task<string> NextNumberAsync(VoucherType type, int year)
    {
        // Serialized via the surrounding transaction; unique index on VoucherNo is the backstop.
        var seq = await db.VoucherSequences.FirstOrDefaultAsync(s => s.Type == type && s.Year == year);
        if (seq is null)
        {
            seq = new VoucherSequence { Type = type, Year = year, NextNumber = 1 };
            db.VoucherSequences.Add(seq);
        }
        var no = $"{Prefixes[type]}-{year}-{seq.NextNumber:D5}";
        seq.NextNumber++;
        return no;
    }
}
