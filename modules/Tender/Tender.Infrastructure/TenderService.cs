using Microsoft.EntityFrameworkCore;
using Tender.Domain;

namespace Tender.Infrastructure;

public interface ITenderService
{
    Task<List<TenderRecord>> ListAsync(string? search = null, TenderStatus? status = null, CancellationToken ct = default);
    Task<TenderRecord?> GetAsync(int id, CancellationToken ct = default);
    Task<TenderRecord> CreateAsync(TenderRecord tender, CancellationToken ct = default);
    Task<TenderRecord> UpdateAsync(TenderRecord tender, CancellationToken ct = default);

    /// <summary>Soft-deletes a tender together with its guarantees and documents.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task<TenderGuarantee> AddGuaranteeAsync(int tenderId, TenderGuarantee guarantee, CancellationToken ct = default);
    Task<TenderGuarantee> UpdateGuaranteeAsync(TenderGuarantee guarantee, CancellationToken ct = default);
    Task DeleteGuaranteeAsync(int id, CancellationToken ct = default);

    Task<TenderDocument> AddDocumentAsync(int tenderId, TenderDocument document, CancellationToken ct = default);
    Task<TenderDocument> UpdateDocumentAsync(TenderDocument document, CancellationToken ct = default);
    Task DeleteDocumentAsync(int id, CancellationToken ct = default);

    Task<TenderCompetitor> AddCompetitorAsync(int tenderId, TenderCompetitor competitor, CancellationToken ct = default);
    Task<TenderCompetitor> UpdateCompetitorAsync(TenderCompetitor competitor, CancellationToken ct = default);
    Task DeleteCompetitorAsync(int id, CancellationToken ct = default);

    /// <summary>Guarantees expiring within the window, active ones only — what the dashboard flags.</summary>
    Task<List<TenderGuarantee>> ListExpiringGuaranteesAsync(int withinDays = 30, CancellationToken ct = default);

    /// <summary>Tenders whose submission deadline is within the window and still open.</summary>
    Task<List<TenderRecord>> ListUpcomingDeadlinesAsync(int withinDays = 14, CancellationToken ct = default);

    /// <summary>Every active/expired guarantee across every tender, for the security register.</summary>
    Task<List<TenderGuarantee>> ListAllGuaranteesAsync(CancellationToken ct = default);
}

public class TenderService(TenderDbContext db) : ITenderService
{
    private static readonly List<TenderStatus> OpenStatuses =
    [
        TenderStatus.Identified, TenderStatus.InPreparation, TenderStatus.Submitted, TenderStatus.TechnicallyQualified
    ];

    public async Task<List<TenderRecord>> ListAsync(
        string? search = null, TenderStatus? status = null, CancellationToken ct = default)
    {
        var q = db.Tenders.Include(t => t.Guarantees).AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(t => t.TenderNumber.Contains(s) || t.Title.Contains(s)
                          || t.IssuingAuthority.Contains(s)
                          || (t.Department != null && t.Department.Contains(s)));
        }

        if (status is { } st) q = q.Where(t => t.Status == st);

        return await q.OrderByDescending(t => t.SubmissionDeadline ?? DateOnly.MinValue)
            .ThenByDescending(t => t.Id)
            .ToListAsync(ct);
    }

    public Task<TenderRecord?> GetAsync(int id, CancellationToken ct = default) =>
        db.Tenders
            .Include(t => t.Guarantees.OrderByDescending(g => g.IssueDate))
            .Include(t => t.Documents.OrderByDescending(d => d.DocumentDate))
            .Include(t => t.Competitors.OrderBy(c => c.Rank))
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<TenderRecord> CreateAsync(TenderRecord tender, CancellationToken ct = default)
    {
        Validate(tender);

        if (await db.Tenders.AnyAsync(t => t.TenderNumber == tender.TenderNumber, ct))
            throw new InvalidOperationException("A tender with this reference number already exists.");

        db.Tenders.Add(tender);
        await db.SaveChangesAsync(ct);
        return tender;
    }

    public async Task<TenderRecord> UpdateAsync(TenderRecord tender, CancellationToken ct = default)
    {
        var existing = await db.Tenders.FirstOrDefaultAsync(t => t.Id == tender.Id, ct)
            ?? throw new InvalidOperationException("Tender not found.");

        Validate(tender);

        if (await db.Tenders.AnyAsync(t => t.TenderNumber == tender.TenderNumber && t.Id != tender.Id, ct))
            throw new InvalidOperationException($"{tender.TenderNumber} is already used by another tender.");

        existing.TenderNumber = tender.TenderNumber;
        existing.Title = tender.Title;
        existing.IssuingAuthority = tender.IssuingAuthority;
        existing.Department = tender.Department;
        existing.Description = tender.Description;
        existing.EstimatedValue = tender.EstimatedValue;
        existing.TenderFee = tender.TenderFee;
        existing.EmdAmount = tender.EmdAmount;
        existing.IsEmdExempted = tender.IsEmdExempted;
        existing.EmdExemptionReason = tender.EmdExemptionReason;
        existing.PerformanceGuaranteePercentage = tender.PerformanceGuaranteePercentage;
        existing.RetentionMoneyPercentage = tender.RetentionMoneyPercentage;
        existing.SubmissionMode = tender.SubmissionMode;
        existing.PortalReference = tender.PortalReference;
        existing.BidValidityDays = tender.BidValidityDays;
        existing.PublishDate = tender.PublishDate;
        existing.SubmissionDeadline = tender.SubmissionDeadline;
        existing.TechnicalOpeningDate = tender.TechnicalOpeningDate;
        existing.FinancialOpeningDate = tender.FinancialOpeningDate;
        existing.Status = tender.Status;
        existing.OurRank = tender.OurRank;
        existing.L1Amount = tender.L1Amount;
        existing.AwardedValue = tender.AwardedValue;
        existing.AwardDate = tender.AwardDate;
        existing.WorkOrderNumber = tender.WorkOrderNumber;
        existing.ContractStartDate = tender.ContractStartDate;
        existing.ContractEndDate = tender.ContractEndDate;
        existing.CompletionPeriodDays = tender.CompletionPeriodDays;
        existing.DefectLiabilityPeriodMonths = tender.DefectLiabilityPeriodMonths;
        existing.PaymentTerms = tender.PaymentTerms;
        existing.ContactPerson = tender.ContactPerson;
        existing.ContactPhone = tender.ContactPhone;
        existing.ContactEmail = tender.ContactEmail;
        existing.Notes = tender.Notes;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    private static void Validate(TenderRecord tender)
    {
        if (string.IsNullOrWhiteSpace(tender.TenderNumber))
            throw new InvalidOperationException("Tender reference number is required.");
        if (string.IsNullOrWhiteSpace(tender.Title))
            throw new InvalidOperationException("Title is required.");
        if (string.IsNullOrWhiteSpace(tender.IssuingAuthority))
            throw new InvalidOperationException("Issuing authority is required.");
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var tender = await db.Tenders
            .Include(t => t.Guarantees).Include(t => t.Documents).Include(t => t.Competitors)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tender is null) return;

        db.Guarantees.RemoveRange(tender.Guarantees);
        db.Documents.RemoveRange(tender.Documents);
        db.Competitors.RemoveRange(tender.Competitors);
        db.Tenders.Remove(tender);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TenderGuarantee> AddGuaranteeAsync(
        int tenderId, TenderGuarantee guarantee, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guarantee.GuaranteeNumber))
            throw new InvalidOperationException("Guarantee/instrument number is required.");
        if (guarantee.Amount <= 0)
            throw new InvalidOperationException("Amount must be greater than zero.");
        if (guarantee.ExpiryDate < guarantee.IssueDate)
            throw new InvalidOperationException("Expiry date cannot be before the issue date.");

        var tender = await db.Tenders.FirstOrDefaultAsync(t => t.Id == tenderId, ct)
            ?? throw new InvalidOperationException("Tender not found.");

        guarantee.TenderRecordId = tender.Id;
        db.Guarantees.Add(guarantee);
        await db.SaveChangesAsync(ct);
        return guarantee;
    }

    public async Task<TenderGuarantee> UpdateGuaranteeAsync(TenderGuarantee guarantee, CancellationToken ct = default)
    {
        var existing = await db.Guarantees.FirstOrDefaultAsync(g => g.Id == guarantee.Id, ct)
            ?? throw new InvalidOperationException("Guarantee not found.");

        if (string.IsNullOrWhiteSpace(guarantee.GuaranteeNumber))
            throw new InvalidOperationException("Guarantee/instrument number is required.");
        if (guarantee.Amount <= 0)
            throw new InvalidOperationException("Amount must be greater than zero.");
        if (guarantee.ExpiryDate < guarantee.IssueDate)
            throw new InvalidOperationException("Expiry date cannot be before the issue date.");

        existing.Type = guarantee.Type;
        existing.InstrumentType = guarantee.InstrumentType;
        existing.BankName = guarantee.BankName;
        existing.BranchName = guarantee.BranchName;
        existing.BankContactPerson = guarantee.BankContactPerson;
        existing.BankContactPhone = guarantee.BankContactPhone;
        existing.GuaranteeNumber = guarantee.GuaranteeNumber;
        existing.Amount = guarantee.Amount;
        existing.Charges = guarantee.Charges;
        existing.IssueDate = guarantee.IssueDate;
        existing.ExpiryDate = guarantee.ExpiryDate;
        existing.ClaimPeriodEndDate = guarantee.ClaimPeriodEndDate;
        existing.Status = guarantee.Status;
        existing.ReleaseDate = guarantee.ReleaseDate;
        existing.ReleaseReference = guarantee.ReleaseReference;
        existing.Remarks = guarantee.Remarks;
        existing.RenewalOfGuaranteeId = guarantee.RenewalOfGuaranteeId;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteGuaranteeAsync(int id, CancellationToken ct = default)
    {
        var guarantee = await db.Guarantees.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (guarantee is null) return;
        db.Guarantees.Remove(guarantee);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TenderDocument> AddDocumentAsync(
        int tenderId, TenderDocument document, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(document.Name))
            throw new InvalidOperationException("Document name is required.");

        var tender = await db.Tenders.FirstOrDefaultAsync(t => t.Id == tenderId, ct)
            ?? throw new InvalidOperationException("Tender not found.");

        document.TenderRecordId = tender.Id;
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);
        return document;
    }

    public async Task<TenderDocument> UpdateDocumentAsync(TenderDocument document, CancellationToken ct = default)
    {
        var existing = await db.Documents.FirstOrDefaultAsync(d => d.Id == document.Id, ct)
            ?? throw new InvalidOperationException("Document not found.");

        if (string.IsNullOrWhiteSpace(document.Name))
            throw new InvalidOperationException("Document name is required.");

        existing.Category = document.Category;
        existing.Name = document.Name;
        existing.ReferenceNumber = document.ReferenceNumber;
        existing.DocumentDate = document.DocumentDate;
        existing.Notes = document.Notes;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteDocumentAsync(int id, CancellationToken ct = default)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return;
        db.Documents.Remove(document);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TenderCompetitor> AddCompetitorAsync(
        int tenderId, TenderCompetitor competitor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(competitor.BidderName))
            throw new InvalidOperationException("Bidder name is required.");

        var tender = await db.Tenders.FirstOrDefaultAsync(t => t.Id == tenderId, ct)
            ?? throw new InvalidOperationException("Tender not found.");

        competitor.TenderRecordId = tender.Id;
        db.Competitors.Add(competitor);
        await db.SaveChangesAsync(ct);
        return competitor;
    }

    public async Task<TenderCompetitor> UpdateCompetitorAsync(TenderCompetitor competitor, CancellationToken ct = default)
    {
        var existing = await db.Competitors.FirstOrDefaultAsync(c => c.Id == competitor.Id, ct)
            ?? throw new InvalidOperationException("Bidder not found.");

        if (string.IsNullOrWhiteSpace(competitor.BidderName))
            throw new InvalidOperationException("Bidder name is required.");

        existing.BidderName = competitor.BidderName;
        existing.QuotedAmount = competitor.QuotedAmount;
        existing.Rank = competitor.Rank;
        existing.IsOwnBid = competitor.IsOwnBid;
        existing.Remarks = competitor.Remarks;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteCompetitorAsync(int id, CancellationToken ct = default)
    {
        var competitor = await db.Competitors.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (competitor is null) return;
        db.Competitors.Remove(competitor);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<TenderGuarantee>> ListAllGuaranteesAsync(CancellationToken ct = default) =>
        await db.Guarantees.Include(g => g.TenderRecord).AsNoTracking()
            .OrderByDescending(g => g.IssueDate)
            .ToListAsync(ct);

    public async Task<List<TenderGuarantee>> ListExpiringGuaranteesAsync(
        int withinDays = 30, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(withinDays);
        return await db.Guarantees.Include(g => g.TenderRecord).AsNoTracking()
            .Where(g => g.Status == GuaranteeStatus.Active && g.ExpiryDate <= cutoff)
            .OrderBy(g => g.ExpiryDate)
            .ToListAsync(ct);
    }

    public async Task<List<TenderRecord>> ListUpcomingDeadlinesAsync(
        int withinDays = 14, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(withinDays);
        return await db.Tenders.AsNoTracking()
            .Where(t => OpenStatuses.Contains(t.Status)
                        && t.SubmissionDeadline != null && t.SubmissionDeadline <= cutoff)
            .OrderBy(t => t.SubmissionDeadline)
            .ToListAsync(ct);
    }
}
