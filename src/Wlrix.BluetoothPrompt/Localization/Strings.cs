using Wlrix.Common.Localization;

namespace Wlrix.BluetoothPrompt.Localization;

/// <summary>
/// Strongly-typed access to the dialog's strings (backed by <c>Strings.resx</c> and its culture
/// satellites).
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.BluetoothPrompt.Localization.Strings", typeof(Strings).Assembly);

    public static string WindowTitle => Catalog.Get("WindowTitle");
    public static string Pair => Catalog.Get("Pair");
    public static string Allow => Catalog.Get("Allow");
    public static string Deny => Catalog.Get("Deny");
    public static string Cancel => Catalog.Get("Cancel");

    /// <summary>
    /// A service as the description names it. <c>wlrix-bluetooth</c> sends a fixed English key
    /// ("audio", "calls"); one it does not know is shown as it came.
    /// </summary>
    public static string ServiceName(string key)
    {
        var name = "Service_" + key.Replace(' ', '_');
        var text = Catalog.Get(name);
        return text == name ? key : text;
    }
}
