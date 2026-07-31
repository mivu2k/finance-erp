namespace Tender.Domain;

public static class TenderPermissions
{
    public const string TendersView = "tender.tenders.view";
    public const string TendersManage = "tender.tenders.manage";
    public const string GuaranteesManage = "tender.guarantees.manage";
    public const string DocumentsManage = "tender.documents.manage";
    public const string ReportsView = "tender.reports.view";

    public static IReadOnlyList<string> All =>
    [
        TendersView, TendersManage, GuaranteesManage, DocumentsManage, ReportsView
    ];
}

public static class TenderRoles
{
    public const string Manager = "Tender Manager";
    public const string Officer = "Tender Officer";
    public const string Viewer = "Tender Viewer";
}
