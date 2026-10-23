// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Wlrix.Common.Desktop;

namespace Wlrix.IconCatalog.Models;

/// <summary>One icon as a page shows it: resolved, named and ready to draw.</summary>
public sealed record CatalogIcon
{
    /// <summary>What the page stores for this icon.</summary>
    public required ItemRef Item { get; init; }

    /// <summary>Unique on a page, and stable across rescans: what a selection is kept by.</summary>
    public string Key => Item.Key;

    /// <summary>The label, in the display language.</summary>
    public required string Name { get; init; }

    /// <summary>The entry's <c>Comment</c>, for the status line, if it has one.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Icon-theme names (or an absolute path) to draw, most wanted first: an application's
    /// symbol, or a plain file's own icon.
    /// </summary>
    public IReadOnlyList<string> IconNames { get; init; } = [];

    /// <summary><c>X-WLRIX-Running-Icon</c>: the symbol to show instead while it runs.</summary>
    public string? RunningIcon { get; init; }

    /// <summary>
    /// The launcher this icon runs, when it is one. Only these stand on a magic carpet: a file
    /// or a directory has no running state to show.
    /// </summary>
    public DesktopEntry? Entry { get; init; }

    /// <summary>A dropped file's path, for anything that is not an application launcher.</summary>
    public string? Path { get; init; }

    /// <summary>Whether this is a plain program file, run directly rather than opened.</summary>
    public bool IsProgram { get; init; }

    /// <summary>Whether to stand it on the control panel carpet rather than the generic one.</summary>
    public bool IsControlPanel => Entry?.IsControlPanel ?? false;

    /// <summary>The names its windows may carry; see <see cref="RunningIdentities"/>.</summary>
    public IReadOnlyList<string> Identities { get; init; } = [];

    /// <summary>The icon for an application launcher.</summary>
    public static CatalogIcon ForEntry(ItemRef item, DesktopEntry entry, CultureInfo culture)
    {
        var comment = DesktopEntryParser.ResolveLocalized(entry.Comment, culture);
        return new CatalogIcon
        {
            Item = item,
            Name = DesktopEntryParser.ResolveLocalized(entry.Name, culture),
            Comment = comment.Length > 0 ? comment : null,
            IconNames = entry.Icon is { Length: > 0 } icon ? [icon] : [],
            RunningIcon = entry.RunningIcon is { Length: > 0 } running ? running : null,
            Entry = entry,
            Identities = RunningIdentities.For(entry),
        };
    }
}
