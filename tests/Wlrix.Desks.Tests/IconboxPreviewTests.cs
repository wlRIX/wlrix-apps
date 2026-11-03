using Wlrix.Desks.Models;
using Wlrix.Desks.ViewModels;
using Xunit;

namespace Wlrix.Desks.Tests;

/// <summary>
/// With the iconbox on, a minimized window has no icon on the desktop for a preview to draw it
/// at, and the compositor reports it where it will reappear instead.
/// </summary>
public class IconboxPreviewTests
{
    private static WindowInfo Window(long id, bool minimized, string appId = "com.wlrix.console") =>
        new(id, 1, 100, 100, 400, 300, minimized, appId, $"window {id}");

    [Fact]
    public void Minimized_windows_are_drawn_at_their_icons_without_an_iconbox()
    {
        IReadOnlyList<WindowInfo> windows = [Window(1, minimized: false), Window(2, minimized: true)];

        Assert.Equal([1L, 2L], MainWindowViewModel.Drawn(windows).Select(w => w.Id));
    }

    [Fact]
    public void Minimized_windows_are_left_out_while_the_iconbox_holds_them()
    {
        IReadOnlyList<WindowInfo> windows =
        [
            Window(1, minimized: false),
            Window(2, minimized: true),
            Window(3, minimized: false, appId: MainWindowViewModel.IconboxAppId),
        ];

        Assert.Equal([1L, 3L], MainWindowViewModel.Drawn(windows).Select(w => w.Id));
    }
}
