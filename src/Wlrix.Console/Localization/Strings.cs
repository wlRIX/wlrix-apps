using Wlrix.Common.Localization;

namespace Wlrix.Console.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The window title and the empty-tab placeholder come through <c>{loc:Tr}</c>, which reads the
/// same <see cref="Catalog"/>. The tab names are here because the tabs are built in code.
///
/// The log *contents* are not localized and will not be: they are the components' own output,
/// written by Rust programs that log in English.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Console.Localization.Strings", typeof(Strings).Assembly);

    public static string TabCompositor => Catalog.Get("TabCompositor");
    public static string TabSession => Catalog.Get("TabSession");
}
