// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Wlrix.Common.Desktop;

namespace Wlrix.IconCatalog.Models;

/// <summary>One notebook page, ready to show.</summary>
/// <param name="Id">A system page's category id, or a custom page's GUID.</param>
/// <param name="Name">The tab label, in the display language.</param>
/// <param name="IsSystem">Whether the catalog made it, so it cannot be renamed or removed.</param>
/// <param name="Icons">Sorted by label.</param>
public sealed record CatalogPage(string Id, string Name, bool IsSystem, IReadOnlyList<CatalogIcon> Icons);

/// <summary>
/// Combines what is installed with what the user changed into the pages the window shows.
/// </summary>
/// <remarks>
/// Pure, given its inputs: the installed entries come from a scan, files from
/// <paramref name="resolveFile"/>. That keeps the rules testable without a disk and lets a
/// rescan rebuild every page at once.
/// </remarks>
public static class PageBuilder
{
    /// <param name="installed">The visible installed applications, as the scanner returns them.</param>
    /// <param name="document">The user's pages and edits.</param>
    /// <param name="pageName">A system page's label, by id.</param>
    /// <param name="resolveFile">
    /// A dropped file's icon, or null when the file is gone. A gone item is skipped but stays in
    /// the state file, so an application that is reinstalled comes back where it was.
    /// </param>
    /// <param name="culture">The display language, for labels and for sorting by them.</param>
    public static IReadOnlyList<CatalogPage> Build(
        IReadOnlyList<DesktopEntry> installed,
        CatalogDocument document,
        Func<string, string> pageName,
        Func<string, CatalogIcon?> resolveFile,
        CultureInfo culture)
    {
        var byId = new Dictionary<string, DesktopEntry>(StringComparer.Ordinal);
        foreach (var entry in installed)
            byId.TryAdd(entry.Id, entry);

        var system = SystemPages.Order.ToDictionary(id => id, _ => new List<CatalogIcon>());
        foreach (var entry in byId.Values)
        {
            var item = ItemRef.Application(entry.Id);
            foreach (var page in SystemPages.For(entry))
                system[page].Add(CatalogIcon.ForEntry(item, entry, culture));
        }

        var byName = StringComparer.Create(culture, ignoreCase: true);
        var pages = new List<CatalogPage>();

        foreach (var id in SystemPages.Order)
        {
            var icons = system[id];
            if (document.SystemPages.TryGetValue(id, out var edits))
            {
                icons.RemoveAll(icon => edits.Hidden.Contains(icon.Key));
                foreach (var added in edits.Added)
                {
                    if (icons.All(icon => icon.Key != added.Key) && Resolve(added) is { } icon)
                        icons.Add(icon);
                }
            }

            // A category nothing installed belongs to is not a page worth a tab.
            if (icons.Count > 0)
                pages.Add(new CatalogPage(id, pageName(id), true, Sorted(icons)));
        }

        foreach (var custom in document.CustomPages)
        {
            var icons = custom.Items.Select(Resolve).OfType<CatalogIcon>().ToList();
            pages.Add(new CatalogPage(custom.Id, custom.Name, false, Sorted(icons)));
        }

        return pages;

        CatalogIcon? Resolve(ItemRef item) => item.Kind switch
        {
            ItemKind.Application => byId.TryGetValue(item.Target, out var entry)
                ? CatalogIcon.ForEntry(item, entry, culture)
                : null,
            _ => resolveFile(item.Target),
        };

        IReadOnlyList<CatalogIcon> Sorted(List<CatalogIcon> icons) =>
            icons.OrderBy(icon => icon.Name, byName).ThenBy(icon => icon.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether the installed applications already put <paramref name="item"/> on a system page.</summary>
    public static bool InstalledOn(string pageId, ItemRef item, IReadOnlyList<DesktopEntry> installed) =>
        item.Kind == ItemKind.Application
        && installed.FirstOrDefault(entry => entry.Id == item.Target) is { } entry
        && SystemPages.For(entry).Contains(pageId);
}
