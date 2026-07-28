namespace GatePass.Domain;

public static class GatePassPermissions
{
    public const string PassesView = "gatepass.passes.view";
    public const string PassesCreate = "gatepass.passes.create";
    public const string PassesAuthorize = "gatepass.passes.authorize";
    /// <summary>Marking goods through at the gate, and recording returns. Security's permission.</summary>
    public const string PassesComplete = "gatepass.passes.complete";
    public const string PassesCancel = "gatepass.passes.cancel";

    public const string DemosView = "gatepass.demos.view";
    public const string DemosManage = "gatepass.demos.manage";
    public const string DemosReturn = "gatepass.demos.return";

    public const string ReportsView = "gatepass.reports.view";

    public static IReadOnlyList<string> All =>
    [
        PassesView, PassesCreate, PassesAuthorize, PassesComplete, PassesCancel,
        DemosView, DemosManage, DemosReturn, ReportsView
    ];
}

public static class GatePassRoles
{
    public const string Manager = "Gate Pass Manager";
    public const string Issuer = "Gate Pass Issuer";
    public const string Security = "Gate Security";
    public const string Viewer = "Gate Pass Viewer";
}
