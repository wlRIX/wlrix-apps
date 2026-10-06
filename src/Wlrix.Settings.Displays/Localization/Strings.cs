using Wlrix.Common.Localization;

namespace Wlrix.Settings.Displays.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c>).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the labels made from a
/// display's own numbers, and the status messages.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Displays.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>"2560 × 1440", optionally with its aspect ratio.</summary>
    public static string Resolution(int width, int height, string? aspect) => aspect is null
        ? Catalog.Format("Resolution", width, height)
        : Catalog.Format("ResolutionAspect", width, height, aspect);

    /// <summary>A resolution label, marked as the one the display asks for.</summary>
    public static string Preferred(string label) => Catalog.Format("PreferredResolution", label);

    /// <summary>"59.95 Hz", from millihertz.</summary>
    public static string Refresh(int milliHz) => Catalog.Format("RefreshRate", milliHz / 1000.0);

    public static string NoCompositor => Catalog.Get("NoCompositor");
    public static string ApplyFailed => Catalog.Get("ApplyFailed");
    public static string ApplyCancelled => Catalog.Get("ApplyCancelled");
    public static string Reverted => Catalog.Get("Reverted");
    public static string RevertFailed => Catalog.Get("RevertFailed");
    public static string DisplaysChanged => Catalog.Get("DisplaysChanged");
    public static string HdrUnsupported => Catalog.Get("HdrUnsupported");
    public static string AdaptiveSyncUnsupported => Catalog.Get("AdaptiveSyncUnsupported");

    /// <summary>"Reverting in 15 seconds."</summary>
    public static string RevertingIn(int seconds) => Catalog.Format("RevertingIn", seconds);
}
