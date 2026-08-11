using System.Drawing;
using FSOps.Desktop;

namespace FSOps.Desktop.Tests;

/// <summary>
/// The rule that stops a remembered window position from becoming a window nobody can find. A
/// laptop undocked from a second monitor, or a display moved from the right of the primary to the
/// left, leaves a saved rectangle pointing at coordinates that no longer exist - and an app that
/// opens entirely off-screen is indistinguishable from one that failed to open at all.
/// </summary>
public class WindowPlacementStoreTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1080);

    [Fact]
    public void A_window_fully_inside_a_screen_is_accepted()
    {
        var placement = new WindowPlacement(200, 150, 1280, 800, Maximized: false);
        Assert.True(WindowPlacementStore.IsOnScreen(placement, [Primary]));
    }

    [Fact]
    public void A_window_on_a_monitor_that_is_no_longer_attached_is_rejected()
    {
        // Saved while a second display sat to the right of the primary; that display is now gone.
        var placement = new WindowPlacement(2400, 200, 1280, 800, Maximized: false);
        Assert.False(WindowPlacementStore.IsOnScreen(placement, [Primary]));
    }

    [Fact]
    public void A_window_on_a_monitor_that_is_still_attached_is_accepted()
    {
        var secondary = new Rectangle(1920, 0, 1920, 1080);
        var placement = new WindowPlacement(2400, 200, 1280, 800, Maximized: false);
        Assert.True(WindowPlacementStore.IsOnScreen(placement, [Primary, secondary]));
    }

    [Fact]
    public void A_window_hanging_barely_off_the_edge_is_rejected()
    {
        // Only a sliver overlaps - not enough of a title bar to grab and drag back.
        var placement = new WindowPlacement(1900, 500, 1280, 800, Maximized: false);
        Assert.False(WindowPlacementStore.IsOnScreen(placement, [Primary]));
    }

    [Fact]
    public void A_window_mostly_off_the_edge_but_still_grabbable_is_accepted()
    {
        var placement = new WindowPlacement(1600, 500, 1280, 800, Maximized: false);
        Assert.True(WindowPlacementStore.IsOnScreen(placement, [Primary]));
    }

    [Fact]
    public void A_window_above_the_top_of_every_screen_is_rejected()
    {
        // The one position a user genuinely cannot recover from with the mouse.
        var placement = new WindowPlacement(400, -900, 1280, 800, Maximized: false);
        Assert.False(WindowPlacementStore.IsOnScreen(placement, [Primary]));
    }

    [Fact]
    public void No_screens_at_all_rejects_every_saved_position()
    {
        var placement = new WindowPlacement(0, 0, 1280, 800, Maximized: false);
        Assert.False(WindowPlacementStore.IsOnScreen(placement, []));
    }
}
