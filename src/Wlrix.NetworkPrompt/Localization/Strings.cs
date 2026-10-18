using Wlrix.Common.Localization;
using Wlrix.NetworkPrompt.Protocol;

namespace Wlrix.NetworkPrompt.Localization;

/// <summary>
/// Strongly-typed access to the dialog's strings (backed by <c>Strings.resx</c> and its culture
/// satellites).
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.NetworkPrompt.Localization.Strings", typeof(Strings).Assembly);

    public static string WindowTitle => Catalog.Get("WindowTitle");
    public static string OtherTitle => Catalog.Get("OtherTitle");
    public static string PasswordHeading => Catalog.Get("PasswordHeading");
    public static string OtherHeading => Catalog.Get("OtherHeading");
    public static string OtherDescription => Catalog.Get("OtherDescription");
    public static string KeyTooShort => Catalog.Get("KeyTooShort");
    public static string KeyTooLong => Catalog.Get("KeyTooLong");
    public static string KeyNotAscii => Catalog.Get("KeyNotAscii");
    public static string KeyWepLength => Catalog.Get("KeyWepLength");

    /// <summary>A security type as Join Other Network's list names it.</summary>
    internal static string SecurityName(Security security) => security switch
    {
        Security.None => Catalog.Get("SecurityNone"),
        Security.WpaPsk => Catalog.Get("SecurityWpa"),
        Security.Sae => Catalog.Get("SecuritySae"),
        Security.Wep => Catalog.Get("SecurityWep"),
        _ => security.ToString(),
    };
}
