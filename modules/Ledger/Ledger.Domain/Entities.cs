namespace Ledger.Domain;

/// <summary>
/// A plain (single-entry) ledger against one counterparty, arranged in a tree.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> the accounting module. It records informal
/// money movements the way a hand-written ledger book does: you took 100,000 from
/// Mr A (one main ledger), then passed 50,000 each to Mr B and Mr C (two
/// sub-ledgers under it), and each of those keeps its own running record.
/// <para>
/// Nesting is unlimited — a sub-ledger can split further — because money passed
/// on rarely stops at one hop. <see cref="ParentLedgerId"/> being null is what
/// makes a ledger a "main" one.
/// </para>
/// </remarks>
public class PlainLedger : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The person or firm this ledger is with. A snapshot, not a foreign key.</summary>
    public string CounterpartyName { get; set; } = string.Empty;
    public string? CounterpartyPhone { get; set; }
    public string? CounterpartyAddress { get; set; }

    /// <summary>
    /// Which way the relationship runs. This is what makes the accounting
    /// unambiguous: on a <see cref="LedgerNature.Payable"/> ledger money coming in
    /// is cash arriving and a debt to them building up, while on a
    /// <see cref="LedgerNature.Receivable"/> ledger money coming in is cash leaving
    /// your hands and a claim on them building up. Without it, "In" would mean
    /// opposite things on the main and the sub ledgers of the same tree.
    /// </summary>
    public LedgerNature Nature { get; set; } = LedgerNature.Receivable;

    /// <summary>Null for a main ledger; set for a sub-ledger.</summary>
    public int? ParentLedgerId { get; set; }
    public PlainLedger? ParentLedger { get; set; }
    public List<PlainLedger> Children { get; set; } = [];

    /// <summary>
    /// Balance carried in from outside the system, so an existing book can be
    /// opened mid-stream. Positive means the counterparty is holding your money.
    /// </summary>
    public decimal OpeningBalance { get; set; }

    public DateOnly OpenedOn { get; set; }
    public LedgerStatus Status { get; set; } = LedgerStatus.Open;
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Optional link to an account in the Finance module's chart of accounts. When
    /// set — and a cash account is configured — entries also post a system voucher
    /// so the movement reaches the real books. Left null, the ledger stays purely
    /// informal and nothing touches the trial balance.
    /// </summary>
    public int? FinanceAccountId { get; set; }

    public List<LedgerEntry> Entries { get; set; } = [];
}

public enum LedgerNature
{
    /// <summary>
    /// You took money from this person — they are owed. "I took 1 lac from Mr A."
    /// A negative balance here means you still owe them.
    /// </summary>
    Payable = 0,
    /// <summary>
    /// You handed money to this person — they owe you. "I gave 50 to Mr B."
    /// A positive balance here means they are still holding your money.
    /// </summary>
    Receivable = 1
}

public enum LedgerStatus
{
    Open = 0,
    /// <summary>Balance is nil and the ledger is done, but kept for the record.</summary>
    Settled = 1,
    /// <summary>Closed with a balance still on it — written off or abandoned.</summary>
    Closed = 2
}

/// <summary>Which way money moved relative to the ledger it sits on.</summary>
public enum LedgerDirection
{
    /// <summary>Money came in to this ledger — it was received or funded.</summary>
    In = 0,
    /// <summary>Money went out of this ledger — it was paid or passed on.</summary>
    Out = 1
}

public enum LedgerEntryKind
{
    /// <summary>Money crossing the boundary of the tree — cash received or paid outside.</summary>
    External = 0,
    /// <summary>
    /// Money moving between two ledgers in the tree. Always written as a linked
    /// pair sharing a <see cref="LedgerEntry.TransferGroup"/>, so the two sides
    /// can never drift apart.
    /// </summary>
    Transfer = 1
}

/// <summary>
/// One line in a plain ledger. Entries are the only thing that moves a balance;
/// a ledger's balance is always reconstructible from them plus its opening figure.
/// </summary>
public class LedgerEntry : AuditableEntity
{
    public int PlainLedgerId { get; set; }
    public PlainLedger PlainLedger { get; set; } = null!;

    public DateOnly Date { get; set; }
    public LedgerDirection Direction { get; set; }
    public LedgerEntryKind Kind { get; set; } = LedgerEntryKind.External;
    public decimal Amount { get; set; }

    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public LedgerPaymentMethod Method { get; set; } = LedgerPaymentMethod.Cash;

    /// <summary>The other ledger on a transfer. Null on an external entry.</summary>
    public int? CounterLedgerId { get; set; }
    public PlainLedger? CounterLedger { get; set; }

    /// <summary>
    /// Shared by the two halves of a transfer. Editing or deleting one half has to
    /// find the other, and an id is the only thing that survives a rename.
    /// </summary>
    public Guid? TransferGroup { get; set; }

    public string RecordedById { get; set; } = string.Empty;
    public string RecordedByName { get; set; } = string.Empty;

    /// <summary>
    /// The Finance voucher this entry posted, when the ledger is mapped to an
    /// account. Null means nothing reached the formal books — either the ledger
    /// isn't mapped, or posting was unavailable when the entry was written.
    /// </summary>
    public int? PostedVoucherId { get; set; }

    /// <summary>Signed effect on the ledger's balance.</summary>
    public decimal Signed => Direction == LedgerDirection.In ? Amount : -Amount;
}

public enum LedgerPaymentMethod { Cash = 0, Bank = 1, Cheque = 2, Online = 3, Adjustment = 4, Other = 5 }
