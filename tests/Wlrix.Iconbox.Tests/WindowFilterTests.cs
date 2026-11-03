using Wlrix.Iconbox.Models;
using Wlrix.Iconbox.Services;
using Xunit;

namespace Wlrix.Iconbox.Tests;

public class WindowFilterTests
{
    private const string Self = "com.wlrix.iconbox";

    private static ToplevelInfo Window(
        long id,
        string appId = "com.wlrix.console",
        bool onVisibleDesk = true,
        bool minimizable = true) =>
        new(id, appId, $"window {id}", Minimized: false, Activated: false, onVisibleDesk, minimizable);

    [Fact]
    public void Windows_on_the_showing_desks_are_listed_in_the_order_they_opened()
    {
        var visible = WindowFilter.Visible([Window(3), Window(1), Window(2)], Self);

        Assert.Equal([1L, 2L, 3L], visible.Select(window => window.Id));
    }

    [Fact]
    public void A_window_on_another_desk_is_left_out()
    {
        var visible = WindowFilter.Visible([Window(1), Window(2, onVisibleDesk: false)], Self);

        Assert.Equal([1L], visible.Select(window => window.Id));
    }

    [Fact]
    public void A_window_that_cannot_be_minimized_has_no_tile()
    {
        // A dialog: it belongs to the window it came from.
        var visible = WindowFilter.Visible([Window(1), Window(2, minimizable: false)], Self);

        Assert.Equal([1L], visible.Select(window => window.Id));
    }

    [Fact]
    public void The_iconbox_does_not_list_itself()
    {
        var visible = WindowFilter.Visible([Window(1, appId: Self), Window(2)], Self);

        Assert.Equal([2L], visible.Select(window => window.Id));
    }
}
