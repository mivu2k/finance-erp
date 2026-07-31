namespace Tender.Domain;

public enum TenderStatus
{
    Identified = 0,
    InPreparation = 1,
    Submitted = 2,
    TechnicallyQualified = 3,
    Won = 4,
    Lost = 5,
    Withdrawn = 6,
    Cancelled = 7
}

/// <summary>
/// What kind of security a guarantee is standing in for. EMD and BidBond both
/// secure the bid itself; the rest secure obligations that only exist once the
/// tender is won, which is why <see cref="TenderGuarantee"/> tracks the type
/// rather than assuming one guarantee per tender.
/// </summary>
public enum GuaranteeType
{
    EarnestMoneyDeposit = 0,
    BidSecurityBond = 1,
    PerformanceGuarantee = 2,
    SecurityDeposit = 3,
    AdvancePaymentGuarantee = 4,
    RetentionMoneyGuarantee = 5,
    Other = 6
}

/// <summary>The physical/financial form the security was lodged in.</summary>
public enum GuaranteeInstrumentType
{
    BankGuarantee = 0,
    DemandDraft = 1,
    FixedDeposit = 2,
    Cheque = 3,
    OnlinePayment = 4,
    InsuranceSuretyBond = 5
}

public enum GuaranteeStatus
{
    Active = 0,
    Released = 1,
    Invoked = 2,
    Expired = 3,
    Refunded = 4
}

public enum DocumentCategory
{
    TenderNotice = 0,
    TechnicalBid = 1,
    FinancialBid = 2,
    EligibilityProof = 3,
    EmdReceipt = 4,
    Compliance = 5,
    Contract = 6,
    Correspondence = 7,
    Other = 8
}

/// <summary>How the bid was lodged with the authority.</summary>
public enum SubmissionMode
{
    Online = 0,
    Offline = 1,
    Both = 2
}

/// <summary>
/// One tender/project opportunity, from notice to contract close-out. The
/// commercial figures (estimated value, EMD/PBG percentages) are the organisation's
/// own numbers for tracking, not a substitute for the guarantees actually lodged —
/// those live on <see cref="Guarantees"/> against the instrument that was issued.
/// </summary>
public class TenderRecord : AuditableEntity
{
    public string TenderNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string IssuingAuthority { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string? Description { get; set; }

    public decimal EstimatedValue { get; set; }
    public decimal? TenderFee { get; set; }
    public decimal? EmdAmount { get; set; }
    public bool IsEmdExempted { get; set; }
    public string? EmdExemptionReason { get; set; }
    public decimal? PerformanceGuaranteePercentage { get; set; }
    public decimal? RetentionMoneyPercentage { get; set; }

    public SubmissionMode SubmissionMode { get; set; } = SubmissionMode.Online;
    public string? PortalReference { get; set; }
    public int? BidValidityDays { get; set; }

    public DateOnly? PublishDate { get; set; }
    public DateOnly? SubmissionDeadline { get; set; }
    public DateOnly? TechnicalOpeningDate { get; set; }
    public DateOnly? FinancialOpeningDate { get; set; }

    public TenderStatus Status { get; set; } = TenderStatus.Identified;

    /// <summary>Our rank at financial opening, 1 being lowest/L1 (or highest, for a reverse auction).</summary>
    public int? OurRank { get; set; }
    public decimal? L1Amount { get; set; }

    public decimal? AwardedValue { get; set; }
    public DateOnly? AwardDate { get; set; }
    public string? WorkOrderNumber { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public int? CompletionPeriodDays { get; set; }
    public int? DefectLiabilityPeriodMonths { get; set; }
    public string? PaymentTerms { get; set; }

    public string? ContactPerson { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public string? Notes { get; set; }

    public List<TenderGuarantee> Guarantees { get; set; } = [];
    public List<TenderDocument> Documents { get; set; } = [];
    public List<TenderCompetitor> Competitors { get; set; } = [];
}

/// <summary>
/// One earnest money deposit, bid bond, bank guarantee or other security instrument
/// lodged against a tender. A tender can carry several over its life — an EMD at
/// bid stage, then a performance guarantee once won — so this is a list, not a
/// handful of fields on the tender itself.
/// </summary>
public class TenderGuarantee : AuditableEntity
{
    public int TenderRecordId { get; set; }
    public TenderRecord TenderRecord { get; set; } = null!;

    public GuaranteeType Type { get; set; }
    public GuaranteeInstrumentType InstrumentType { get; set; } = GuaranteeInstrumentType.BankGuarantee;

    public string? BankName { get; set; }
    public string? BranchName { get; set; }
    public string? BankContactPerson { get; set; }
    public string? BankContactPhone { get; set; }
    public string GuaranteeNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    /// <summary>Commission/charges the bank levied to issue this instrument — a real cost of bidding.</summary>
    public decimal? Charges { get; set; }

    public DateOnly IssueDate { get; set; }
    public DateOnly ExpiryDate { get; set; }

    /// <summary>Bank guarantees standardly carry a claim window past expiry.</summary>
    public DateOnly? ClaimPeriodEndDate { get; set; }

    public GuaranteeStatus Status { get; set; } = GuaranteeStatus.Active;
    public DateOnly? ReleaseDate { get; set; }
    public string? ReleaseReference { get; set; }
    public string? Remarks { get; set; }

    /// <summary>
    /// Set when this instrument was issued to extend/replace an earlier one (a BG
    /// renewed before it lapses) — keeps the renewal chain visible instead of the
    /// old instrument just quietly disappearing from the active list.
    /// </summary>
    public int? RenewalOfGuaranteeId { get; set; }
    public TenderGuarantee? RenewalOfGuarantee { get; set; }
}

/// <summary>
/// A record of a document related to the tender — the notice itself, the bid
/// submitted, correspondence, the EMD receipt. This tracks reference metadata for
/// the paper trail; it is not a file store.
/// </summary>
public class TenderDocument : AuditableEntity
{
    public int TenderRecordId { get; set; }
    public TenderRecord TenderRecord { get; set; } = null!;

    public DocumentCategory Category { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ReferenceNumber { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// One bidder at the financial opening, ourselves included. This is what a
/// comparative statement is built from — the platform's own commercial figures on
/// <see cref="TenderRecord"/> track our estimate, not the field of bids actually
/// received.
/// </summary>
public class TenderCompetitor : AuditableEntity
{
    public int TenderRecordId { get; set; }
    public TenderRecord TenderRecord { get; set; } = null!;

    public string BidderName { get; set; } = string.Empty;
    public decimal QuotedAmount { get; set; }
    public int? Rank { get; set; }
    public bool IsOwnBid { get; set; }
    public string? Remarks { get; set; }
}
