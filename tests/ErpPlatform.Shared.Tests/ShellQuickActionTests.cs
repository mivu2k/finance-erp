using ErpPlatform.Shared.Web.Layout;
using Xunit;

namespace ErpPlatform.Shared.Tests;

/// <summary>
/// The app bar's module shortcuts. Small, but the registry is process-wide static, so
/// the two things worth pinning are that a module can't double-register and that order
/// is deterministic — a button that moves between renders is worse than no button.
/// </summary>
public class ShellQuickActionTests
{
    [Fact]
    public void Registering_the_same_key_twice_replaces_rather_than_duplicates()
    {
        ShellQuickActions.Register(new ShellQuickAction(
            "test.dup", "First", "icon", "/a", Order: 5));
        ShellQuickActions.Register(new ShellQuickAction(
            "test.dup", "Second", "icon", "/b", Order: 5));

        var matches = ShellQuickActions.All.Where(a => a.Key == "test.dup").ToList();
        Assert.Single(matches);
        Assert.Equal("/b", matches[0].Href);
    }

    [Fact]
    public void Actions_come_back_in_a_stable_order()
    {
        ShellQuickActions.Register(new ShellQuickAction("test.z", "Zebra", "i", "/z", Order: 50));
        ShellQuickActions.Register(new ShellQuickAction("test.a", "Alpha", "i", "/a", Order: 10));

        var keys = ShellQuickActions.All.Select(a => a.Key).ToList();
        Assert.True(keys.IndexOf("test.a") < keys.IndexOf("test.z"));
    }

    [Fact]
    public void A_null_policy_means_everyone_signed_in()
    {
        var action = new ShellQuickAction("test.open", "Open", "i", "/open");
        Assert.Null(action.Policy);
        Assert.Equal(100, action.Order);
    }
}
