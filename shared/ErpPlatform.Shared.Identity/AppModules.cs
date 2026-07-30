namespace ErpPlatform.Shared.Identity;

/// <summary>
/// The catalog of applications on the platform. This is the code-side source of
/// truth; <see cref="ModuleDefinition"/> rows are seeded into the identity
/// database from it so access can be granted per module in the admin UI.
/// </summary>
public static class AppModules
{
    public const string Finance = "finance";
    public const string Repair = "repair";
    public const string GatePass = "gatepass";
    public const string Hr = "hr";
    public const string Inventory = "inventory";
    public const string Auto = "auto";
    public const string Ledger = "ledger";

    /// <summary>Every module tile the portal can show, in display order.</summary>
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new(Finance, "Finance", "Accounts, vouchers, ledger, payroll and reports.",
            "/finance", "Payments", "#1976d2", 1),
        new(Repair, "Repair", "Customer intakes, repair jobs, quotations and invoicing.",
            "/repair", "Build", "#f57c00", 2),
        new(GatePass, "Gate Pass & Demo Goods", "Inward/outward gate passes and demo issuances.",
            "/gatepass", "LocalShipping", "#388e3c", 3),
        new(Hr, "HR", "Employee master data, departments and documents.",
            "/hr", "Badge", "#7b1fa2", 4),
        new(Inventory, "Inventory", "Products, models, accessories and stock tracking.",
            "/inventory", "Inventory2", "#00897b", 5),
        new(Auto, "Auto", "Company vehicle fleet and maintenance tracking.",
            "/auto", "DirectionsCar", "#5d4037", 6),
        new(Ledger, "Plain Ledger", "Hand-ledger books: main ledgers, sub-ledgers and transfers.",
            "/ledger", "AccountTree", "#c2185b", 7)
    ];

    public static ModuleDefinition? Find(string key) =>
        All.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps a request path back to the module that owns it.</summary>
    public static ModuleDefinition? FromPath(string? path) =>
        string.IsNullOrEmpty(path)
            ? null
            : All.FirstOrDefault(m =>
                path.StartsWith(m.BasePath + "/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(m.BasePath, StringComparison.OrdinalIgnoreCase));
}

/// <param name="Key">Stable identifier stored against roles and access grants.</param>
/// <param name="Name">Tile caption.</param>
/// <param name="BasePath">Route prefix the module's pages live under.</param>
/// <param name="Icon">MudBlazor icon name (resolved by the portal).</param>
public record ModuleDefinition(
    string Key,
    string Name,
    string Description,
    string BasePath,
    string Icon,
    string Color,
    int SortOrder);
