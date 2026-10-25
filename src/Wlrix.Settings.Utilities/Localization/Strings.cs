// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Common.Localization;

namespace Wlrix.Settings.Utilities.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the windows come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the menu labels and headings
/// (looked up by the roles' keys), the dialogs' words, and the messages that carry a detail.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Utilities.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>The last item of every menu, which opens the chooser.</summary>
    public static string Other => Catalog.Get("Other");

    /// <summary>A menu whose role nothing is chosen for.</summary>
    public static string None => Catalog.Get("None");

    public static string ChooserTitle => Catalog.Get("ChooserTitle");

    public static string AddTypeTitle => Catalog.Get("AddTypeTitle");

    public static string AddTypePrompt => Catalog.Get("AddTypePrompt");

    public static string AddPatternTitle => Catalog.Get("AddPatternTitle");

    public static string AddPatternPrompt => Catalog.Get("AddPatternPrompt");

    public static string CloseTitle => Catalog.Get("CloseTitle");

    public static string ClosePrompt => Catalog.Get("ClosePrompt");

    public static string Apply => Catalog.Get("Apply");

    public static string Discard => Catalog.Get("Discard");

    /// <summary>Apply wrote what it could; <paramref name="detail"/> is what it could not.</summary>
    public static string ApplyFailed(string detail) => Catalog.Format("ApplyFailed", detail);

    /// <summary>A new type's name is not <c>media/subtype</c> in a known media group.</summary>
    public static string InvalidType(string type) => Catalog.Format("InvalidType", type);

    /// <summary>A new type's name is one there already is.</summary>
    public static string TypeExists(string type) => Catalog.Format("TypeExists", type);
}
