// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Desktop;

namespace Wlrix.IconCatalog.Models;

/// <summary>
/// Matching a window to a launcher, the way <c>wlrix-desktop</c>'s <c>running.rs</c> does it.
/// </summary>
/// <remarks>
/// A window carries an <c>app_id</c>; a <c>.desktop</c> file does not, quite. The spec's
/// convention is that <c>app_id</c> equals the desktop file's basename, and
/// <c>StartupWMClass</c> exists for the applications that break it. So three candidates are
/// tried, most reliable first, and compared case-insensitively because <c>Alacritty</c> and
/// <c>alacritty</c> are the same application by any sensible reading. Kept in step with the
/// desktop's by hand, so a carpet stands up in both places at once.
/// </remarks>
public static class RunningIdentities
{
    /// <summary>
    /// The names a launcher's windows might appear under, lowercased: <c>StartupWMClass</c>,
    /// then the desktop file's basename, then the <c>Exec</c> line's program name.
    /// </summary>
    public static IReadOnlyList<string> For(DesktopEntry entry)
    {
        var names = new List<string>();

        Push(entry.StartupWmClass);
        Push(Path.GetFileNameWithoutExtension(entry.FilePath ?? entry.Id));
        if (entry.Exec is { } exec && ExecParser.Parse(exec) is { } parsed)
            Push(Path.GetFileName(parsed.File));

        return names;

        void Push(string? name)
        {
            name = name?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                names.Add(name);
        }
    }

    /// <summary>
    /// Whether any open window's <c>app_id</c> (lowercased) is one of <paramref name="identities"/>,
    /// or is a reverse-DNS id whose last component is.
    /// </summary>
    /// <remarks>
    /// The last-component rule is for applications that name their window in reverse-DNS form
    /// while their launcher does not: Dolphin Emulator's window is
    /// <c>org.DolphinEmu.dolphin-emu</c>, its <c>dolphin-emu.desktop</c> says nothing more.
    /// </remarks>
    public static bool AnyOpen(IReadOnlyList<string> identities, IReadOnlySet<string> openAppIds) =>
        identities.Any(openAppIds.Contains)
        || openAppIds.Any(appId => LastComponent(appId) is { } last && identities.Contains(last));

    private static string? LastComponent(string appId)
    {
        var dot = appId.LastIndexOf('.');
        return dot < 0 ? null : appId[(dot + 1)..];
    }
}
