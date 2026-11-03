using Wlrix.Iconbox.Models;

namespace Wlrix.Iconbox.Services;

/// <summary>Which windows get a tile.</summary>
public static class WindowFilter
{
    /// <summary>
    /// The windows to show, in the order the compositor announced them.
    /// </summary>
    /// <remarks>
    /// The same rule the compositor's desktop tiles follow: the active desk and the global desk,
    /// and only windows that can be minimized -- a dialog belongs to the window it came from,
    /// and has no tile of its own. The iconbox itself is left out; it cannot be minimized, and
    /// a tile for the window you are looking at would only restate it.
    /// </remarks>
    public static IReadOnlyList<ToplevelInfo> Visible(IEnumerable<ToplevelInfo> windows, string selfAppId) =>
        [
            .. windows
                .Where(window => window.OnVisibleDesk && window.Minimizable && window.AppId != selfAppId)
                .OrderBy(window => window.Id),
        ];
}
