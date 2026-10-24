using Wlrix.Common.Localization;

namespace Wlrix.Settings.International.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels come through <c>{loc:Tr}</c>, which reads the same <see cref="Catalog"/>.
/// The names in the lists -- countries, languages, layouts -- are not here: they come translated
/// from iso-codes, ICU and xkeyboard-config.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.International.Localization.Strings", typeof(Strings).Assembly);

    public static string ChooseCountryPrompt => Catalog.Get("ChooseCountryPrompt");
    public static string NtpUnavailable => Catalog.Get("NtpUnavailable");
    public static string NtpSynchronized => Catalog.Get("NtpSynchronized");
    public static string NtpWaiting => Catalog.Get("NtpWaiting");
    public static string NtpOff => Catalog.Get("NtpOff");
    public static string LanguageAtNextLogin => Catalog.Get("LanguageAtNextLogin");
    public static string TimeZoneIsSystemWide => Catalog.Get("TimeZoneIsSystemWide");
    public static string Saved => Catalog.Get("Saved");
    public static string NotAuthorized => Catalog.Get("NotAuthorized");
    public static string ClockSet => Catalog.Get("ClockSet");
    public static string ForCountry(string country) => Catalog.Format("ForCountry", country);
    public static string AllEntries => Catalog.Get("AllEntries");
    public static string SameAsLanguage => Catalog.Get("SameAsLanguage");
    public static string NotGenerated => Catalog.Get("NotGenerated");
    public static string DefaultVariant => Catalog.Get("DefaultVariant");
    public static string NoSwitchKey => Catalog.Get("NoSwitchKey");
    public static string BadDateTime => Catalog.Get("BadDateTime");

    public static string CouldNotRead(string path, string detail) => Catalog.Format("CouldNotRead", path, detail);
    public static string CouldNotWrite(string path, string detail) => Catalog.Format("CouldNotWrite", path, detail);
    public static string NoSettingsService(string detail) => Catalog.Format("NoSettingsService", detail);
    public static string NoSystemServices(string detail) => Catalog.Format("NoSystemServices", detail);
    public static string Generating(string language) => Catalog.Format("Generating", language);
    public static string GenerateFailed(string locale, string detail) => Catalog.Format("GenerateFailed", locale, detail);
}
