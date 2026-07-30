namespace Ledger.Domain;

public static class LedgerPermissions
{
    public const string View = "ledger.ledgers.view";
    public const string Manage = "ledger.ledgers.manage";
    /// <summary>Writing entries and transfers. Separate from editing the ledger itself.</summary>
    public const string EntryRecord = "ledger.entries.record";
    /// <summary>Correcting or removing an entry already written.</summary>
    public const string EntryAmend = "ledger.entries.amend";
    public const string ReportsView = "ledger.reports.view";
    /// <summary>Mapping a ledger onto a Finance account so entries post to the books.</summary>
    public const string FinanceLink = "ledger.finance.link";

    public static IReadOnlyList<string> All =>
    [
        View, Manage, EntryRecord, EntryAmend, ReportsView, FinanceLink
    ];
}

public static class LedgerRoles
{
    public const string Manager = "Ledger Manager";
    public const string Clerk = "Ledger Clerk";
    public const string Viewer = "Ledger Viewer";
}
