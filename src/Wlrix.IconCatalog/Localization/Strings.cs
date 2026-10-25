// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Localization;

namespace Wlrix.IconCatalog.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to choose between or build.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.IconCatalog.Localization.Strings", typeof(Strings).Assembly);

    public static string AppName => Catalog.Get("AppName");
    public static string AboutTitle => Catalog.Get("AboutTitle");
    public static string TabsHint => Catalog.Get("TabsHint");
    public static string NoPages => Catalog.Get("NoPages");
    public static string NewPageTitle => Catalog.Get("NewPageTitle");
    public static string NewPagePrompt => Catalog.Get("NewPagePrompt");
    public static string RenamePageTitle => Catalog.Get("RenamePageTitle");
    public static string RemovePageTitle => Catalog.Get("RemovePageTitle");
    public static string DialogOk => Catalog.Get("DialogOk");
    public static string DialogCancel => Catalog.Get("DialogCancel");
    public static string ErrorTitle => Catalog.Get("ErrorTitle");
    public static string NoTerminalForApp => Catalog.Get("NoTerminalForApp");

    /// <summary>The window title: the page, and where it is among the others.</summary>
    public static string Title(string page, int number, int count) => Catalog.Format("Title", page, number, count);

    /// <summary>The product information text, with the version.</summary>
    public static string About(string version) => Catalog.Format("About", version);

    /// <summary>The status line's count of the icons on the page.</summary>
    public static string IconsShown(int count) => Catalog.Format("IconsShown", count);

    public static string RenamePagePrompt(string page) => Catalog.Format("RenamePagePrompt", page);
    public static string RemovePageConfirm(string page) => Catalog.Format("RemovePageConfirm", page);

    public static string NoRunnableCommand(string exec) => Catalog.Format("NoRunnableCommand", exec);
    public static string CouldNotStart(string name, string detail) => Catalog.Format("CouldNotStart", name, detail);

    public static string EditTitle => Catalog.Get("EditTitle");
    public static string EditRevert => Catalog.Get("EditRevert");
    public static string EditNoCommand => Catalog.Get("EditNoCommand");
    public static string EditSavesCopy(string directory) => Catalog.Format("EditSavesCopy", directory);
    public static string EditSavesFile(string path) => Catalog.Format("EditSavesFile", path);
    public static string EditSavesOverride(string directory) => Catalog.Format("EditSavesOverride", directory);
    public static string EditBadVariable(string line) => Catalog.Format("EditBadVariable", line);
    public static string EditSaveFailed(string path, string detail) => Catalog.Format("EditSaveFailed", path, detail);

    /// <summary>A system page's tab label, by its id (<c>Page_&lt;id&gt;</c>).</summary>
    public static string Page(string id) => Catalog.Get("Page_" + id);
}
