using Wlrix.Common.Localization;

namespace Wlrix.Settings.Network.Localization;

/// <summary>
/// The panel's strings (backed by <c>Strings.resx</c> and its culture satellites). Most are
/// reached through <c>{loc:Tr}</c> in the window and <see cref="Catalog"/> in the view model.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Network.Localization.Strings", typeof(Strings).Assembly);
}
