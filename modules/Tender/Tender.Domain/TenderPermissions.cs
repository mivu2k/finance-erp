namespace Tender.Domain;

public static class TenderPermissions
{
    public const string TendersView = "tender.tenders.view";
    public const string TendersManage = "tender.tenders.manage";
    public const string GuaranteesManage = "tender.guarantees.manage";
    public const string DocumentsManage = "tender.documents.manage";
    public const string ReportsView = "tender.reports.view";

    public const string ProjectsView = "tender.projects.view";
    public const string ProjectsManage = "tender.projects.manage";

    /// <summary>
    /// Separate from <see cref="ProjectsManage"/> so a team member can move their own
    /// work along without being able to create, re-scope or delete the project itself.
    /// </summary>
    public const string TasksManage = "tender.tasks.manage";

    public static IReadOnlyList<string> All =>
    [
        TendersView, TendersManage, GuaranteesManage, DocumentsManage, ReportsView,
        ProjectsView, ProjectsManage, TasksManage
    ];
}

public static class TenderRoles
{
    public const string Manager = "Tender Manager";
    public const string Officer = "Tender Officer";
    public const string Viewer = "Tender Viewer";
    public const string ProjectManager = "Project Manager";
    public const string ProjectMember = "Project Member";
}
