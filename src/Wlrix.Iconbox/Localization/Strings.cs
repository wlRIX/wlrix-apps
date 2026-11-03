using Wlrix.Common.Localization;

namespace Wlrix.Iconbox.Localization;

/// <summary>
/// The app's localized UI strings (backed by <c>Strings.resx</c> and its culture satellites).
/// Everything is static text, read through <c>{loc:Tr}</c>.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind <c>{loc:Tr}</c>.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Iconbox.Localization.Strings", typeof(Strings).Assembly);
}
