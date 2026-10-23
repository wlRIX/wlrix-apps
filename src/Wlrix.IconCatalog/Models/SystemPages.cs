// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Desktop;

namespace Wlrix.IconCatalog.Models;

/// <summary>
/// The pages the catalog fills by itself, from what is installed: one per freedesktop main
/// category, with ControlPanels first.
/// </summary>
/// <remarks>
/// The ids are the category names, and are what the state file and the string resources
/// (<c>Page_&lt;id&gt;</c>) key on. A custom page's id is a GUID, so the two never collide.
/// </remarks>
public static class SystemPages
{
    public const string ControlPanels = "ControlPanels";

    /// <summary>Every system page, in tab order.</summary>
    public static IReadOnlyList<string> Order { get; } =
    [
        ControlPanels, "AudioVideo", "Development", "Education", "Game", "Graphics",
        "Network", "Office", "Science", "System", "Utility", MainCategory.Other,
    ];

    private static readonly HashSet<string> Known = new(Order, StringComparer.Ordinal);

    public static bool IsSystem(string pageId) => Known.Contains(pageId);

    /// <summary>The pages an installed application appears on.</summary>
    /// <remarks>
    /// Every main category it names, not just the first: Steam is both a game and a network
    /// application, and is looked for under both. One of wlRIX's own control panels goes only
    /// on ControlPanels, as IRIX's did; any other <c>Settings</c> tool goes there as well as on
    /// its other pages.
    /// </remarks>
    public static IReadOnlyList<string> For(DesktopEntry entry)
    {
        if (entry.IsControlPanel)
            return [ControlPanels];

        return MainCategory.ClassifyAll(entry.Categories)
            .Select(category => category == "Settings" ? ControlPanels : category)
            .Distinct()
            .OrderBy(page => Order.IndexOf(page))
            .ToList();
    }

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return i;
        }
        return -1;
    }
}
