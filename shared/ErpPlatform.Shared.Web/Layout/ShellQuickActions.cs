namespace ErpPlatform.Shared.Web.Layout;

/// <summary>
/// A shortcut a module wants on the platform app bar, reachable from every page of
/// every app.
/// </summary>
/// <param name="Key">Stable identifier, so a module can't register the same action twice.</param>
/// <param name="Title">Tooltip text. The bar shows an icon only.</param>
/// <param name="Icon">A MudBlazor icon constant.</param>
/// <param name="Href">Where it goes.</param>
/// <param name="Policy">
/// Permission the user must hold for the button to appear. Null means everyone signed in.
/// </param>
/// <param name="Order">Lower sorts left.</param>
public record ShellQuickAction(
    string Key,
    string Title,
    string Icon,
    string Href,
    string? Policy = null,
    int Order = 100);

/// <summary>
/// The app bar's shortcuts, registered by the host at startup.
/// </summary>
/// <remarks>
/// Exists so the shared shell doesn't have to know about any particular module. The
/// thing that prompted it: an employee's rotating attendance QR lived four clicks deep
/// — portal, HR, find it in the nav — which is three clicks too many for something
/// people use at a door twice a day with a queue behind them.
/// <para>
/// Registration happens in the host's composition root rather than inside a module,
/// because that is the only place that legitimately knows about both the shell and the
/// module. <c>Shared.Web</c> stays free of module references, and a module's
/// Infrastructure project stays free of a UI reference.
/// </para>
/// </remarks>
public static class ShellQuickActions
{
    private static readonly Dictionary<string, ShellQuickAction> Actions = [];
    private static readonly Lock Gate = new();

    public static void Register(ShellQuickAction action)
    {
        lock (Gate) Actions[action.Key] = action;
    }

    public static IReadOnlyList<ShellQuickAction> All
    {
        get
        {
            lock (Gate)
                return Actions.Values.OrderBy(a => a.Order).ThenBy(a => a.Title).ToList();
        }
    }
}
