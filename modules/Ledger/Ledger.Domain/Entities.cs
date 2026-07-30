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
    /// Which way the relationship runs, which is what tells a balance apart from
    /// its opposite: money sitting on a <see cref="LedgerNature.Payable"/> ledger is
    /// money you owe, the same figure on a <see cref="LedgerNature.Receivable"/> one
    /// is money owed to you. Without it the two would read identically.
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
    /// Optional head this whole book is filed under — "Loans Taken", "Advances
    /// Given". The module keeps its own heads (<see cref="LedgerHead"/>); nothing
    /// here refers to the accounting module's chart of accounts.
    /// </summary>
    public int? HeadId { get; set; }
    public LedgerHead? Head { get; set; }

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

    /// <summary>
    /// What this particular movement was for — "Rent", "Salary", "Repayment".
    /// Independent of the ledger's own head, so a book filed under one head can
    /// still have entries spread across several.
    /// </summary>
    public int? HeadId { get; set; }
    public LedgerHead? Head { get; set; }

    public string RecordedById { get; set; } = string.Empty;
    public string RecordedByName { get; set; } = string.Empty;

    /// <summary>Signed effect on the ledger's balance.</summary>
    public decimal Signed => Direction == LedgerDirection.In ? Amount : -Amount;
}

public enum LedgerPaymentMethod { Cash = 0, Bank = 1, Cheque = 2, Online = 3, Adjustment = 4, Other = 5 }

/// <summary>
/// A head money is filed under — this module's own classification, nothing to do
/// with the accounting module's chart of accounts.
/// </summary>
/// <remarks>
/// Heads nest, so broad groupings can carry detail underneath ("Expenses" over
/// "Rent" and "Utilities") and reports roll a parent up from its children. A head
/// applies to a whole book (<see cref="PlainLedger.HeadId"/>) and to individual
/// movements (<see cref="LedgerEntry.HeadId"/>); both are optional, so heads can be
/// introduced to an existing set of books without going back over them.
/// </remarks>
public class LedgerHead : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }

    /// <summary>Null for a top-level head.</summary>
    public int? ParentHeadId { get; set; }
    public LedgerHead? ParentHead { get; set; }
    public List<LedgerHead> Children { get; set; } = [];

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}
