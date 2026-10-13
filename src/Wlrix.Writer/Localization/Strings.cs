// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Localization;

namespace Wlrix.Writer.Localization;

/// <summary>The application's strings.</summary>
/// <remarks>
/// Hand-written rather than generated, matching the other localized apps. Menus and labels use
/// <c>{loc:Tr}</c> against <see cref="Catalog"/> directly; the members here are for the strings
/// put together in C#.
/// </remarks>
public static class Strings
{
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Writer.Localization.Strings", typeof(Strings).Assembly);

    public static string AppName => Catalog.Get("AppName");
    public static string Untitled => Catalog.Get("Untitled");
    public static string TitleModified(string name) => Catalog.Format("TitleModified", name);
    public static string TabModified(string name) => Catalog.Format("TabModified", name);

    public static string Plain => Catalog.Get("PrefPlain");
    public static string OpenPreviousNone => Catalog.Get("FileOpenPreviousNone");

    public static string OpenTitle => Catalog.Get("OpenTitle");
    public static string SaveAsTitle => Catalog.Get("SaveAsTitle");
    public static string FilterAllFiles => Catalog.Get("FilterAllFiles");
    public static string FilterText => Catalog.Get("FilterText");

    public static string ErrorTitle => Catalog.Get("ErrorTitle");
    public static string InfoTitle => Catalog.Get("InfoTitle");
    public static string AboutTitle => Catalog.Get("AboutTitle");
    public static string About(string version) => Catalog.Format("AboutMessage", version);
    public static string OpenFailed(string name, string reason) => Catalog.Format("ErrorOpenFailed", name, reason);
    public static string NotText(string name) => Catalog.Format("ErrorNotText", name);
    public static string SaveFailed(string name, string reason) => Catalog.Format("ErrorSaveFailed", name, reason);
    public static string EncodingWidened(string name, string encoding) =>
        Catalog.Format("InfoEncodingWidened", name, encoding);

    public static string SaveChangesTitle => Catalog.Get("SaveChangesTitle");
    public static string SaveChanges(string name) => Catalog.Format("SaveChangesMessage", name);
    public static string Save => Catalog.Get("DialogSave");
    public static string DontSave => Catalog.Get("DialogDontSave");
    public static string Cancel => Catalog.Get("DialogCancel");
    public static string Ok => Catalog.Get("DialogOk");

    public static string ReloadTitle => Catalog.Get("ReloadTitle");
    public static string Reload(string name, bool modified) =>
        Catalog.Format(modified ? "ReloadMessageModified" : "ReloadMessage", name);
    public static string ReloadButton => Catalog.Get("DialogReload");
    public static string KeepButton => Catalog.Get("DialogKeep");
    public static string DeletedTitle => Catalog.Get("DeletedTitle");
    public static string Deleted(string name) => Catalog.Format("DeletedMessage", name);

    public static string TabsTitle => Catalog.Get("TabsTitle");
}
