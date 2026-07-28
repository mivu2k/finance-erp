using MudBlazor;

namespace ErpPlatform.Shared.Web;

/// <summary>
/// Resolves the icon name stored against a module to a MudBlazor icon path.
/// Keeping this a lookup rather than reflection means a typo in the catalog
/// degrades to a default icon instead of throwing at render time.
/// </summary>
public static class ModuleIcons
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Payments"] = Icons.Material.Filled.Payments,
        ["Build"] = Icons.Material.Filled.Build,
        ["LocalShipping"] = Icons.Material.Filled.LocalShipping,
        ["Badge"] = Icons.Material.Filled.Badge,
        ["Fingerprint"] = Icons.Material.Filled.Fingerprint,
        ["Inventory"] = Icons.Material.Filled.Inventory,
        ["Groups"] = Icons.Material.Filled.Groups,
        ["Settings"] = Icons.Material.Filled.Settings
    };

    public static string Resolve(string? name) =>
        name is not null && Map.TryGetValue(name, out var icon) ? icon : Icons.Material.Filled.Apps;
}
