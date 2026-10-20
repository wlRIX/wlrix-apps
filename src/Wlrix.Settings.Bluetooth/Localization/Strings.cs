using Wlrix.Common.Localization;

namespace Wlrix.Settings.Bluetooth.Localization;

/// <summary>
/// Strongly-typed access to the panel's strings (backed by <c>Strings.resx</c> and its culture
/// satellites). The static labels come through <c>{loc:Tr}</c>; what is here is what code builds.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Bluetooth.Localization.Strings", typeof(Strings).Assembly);
}
