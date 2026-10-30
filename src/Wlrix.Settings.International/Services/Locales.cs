using System.Diagnostics;
using System.Globalization;
using Wlrix.Settings.International.Core;
using Wlrix.Common;

namespace Wlrix.Settings.International.Services;

/// <summary>One locale glibc can build, and whether it has been.</summary>
/// <param name="Name">What <c>LANG</c> is set to, e.g. <c>ja_JP.UTF-8</c>.</param>
/// <param name="Charset">The charset glibc builds it in; always UTF-8 in this panel's lists.</param>
/// <param name="IsGenerated">Built on this machine, so programs can use it today.</param>
/// <param name="DisplayName">The language and country in the UI language, e.g. ドイツ語 (ドイツ).</param>
public sealed record LocaleEntry(string Name, string Charset, bool IsGenerated, string DisplayName)
{
    /// <summary>The language code, e.g. <c>ja</c>.</summary>
    public string Language => Locales.Split(Name).Language;

    /// <summary>The ISO 3166 country code, e.g. <c>JP</c>; empty for a locale with none.</summary>
    public string Territory => Locales.Split(Name).Territory;

    /// <summary>What a list row shows: the readable name, then the locale itself.</summary>
    public string Display => $"{DisplayName}  —  {Name}";

    public SupportedLocale Supported => new(Name, Charset);
}

/// <summary>
/// The locales the Language dialog offers: glibc's whole <c>SUPPORTED</c> list in UTF-8, each
/// marked with whether <c>locale-gen</c> has built it yet.
/// </summary>
/// <remarks>
/// UTF-8 only. The ISO-8859 and EUC variants are for systems older than anything wlRIX runs on,
/// and every component here assumes UTF-8 text.
/// </remarks>
internal static class Locales
{
    /// <summary>Every UTF-8 locale, sorted by its readable name.</summary>
    public static IReadOnlyList<LocaleEntry> Load()
    {
        IReadOnlyList<SupportedLocale> supported;
        try
        {
            supported = LocaleGen.ParseSupported(File.ReadAllText(LocaleGen.SupportedPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            supported = [];
        }
        return Build(supported, Generated());
    }

    /// <summary>The UTF-8 entries of <paramref name="supported"/>, marked against <c>locale -a</c>'s output.</summary>
    public static IReadOnlyList<LocaleEntry> Build(IEnumerable<SupportedLocale> supported, IEnumerable<string> generated)
    {
        var built = generated.Select(Key).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. supported
                .Where(locale => IsUtf8(locale.Charset))
                .Select(locale => new LocaleEntry(locale.Name, locale.Charset,
                    built.Contains(Key(locale.Name)), DisplayNameOf(locale.Name)))
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>
    /// The entry for <paramref name="name"/> however it is spelled: <c>LANG</c> may say
    /// <c>ja_JP.utf8</c> where the list says <c>ja_JP.UTF-8</c>.
    /// </summary>
    public static LocaleEntry? Find(IEnumerable<LocaleEntry> locales, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        var key = Key(name);
        return locales.FirstOrDefault(locale => locale.Name == name)
               ?? locales.FirstOrDefault(locale => Key(locale.Name) == key);
    }

    /// <summary>What <c>locale -a</c> lists. Empty if it cannot be run, which marks everything ungenerated.</summary>
    public static IReadOnlyList<string> Generated()
    {
        try
        {
            var info = Executables.StartInfoFor("locale");
            info.ArgumentList.Add("-a");
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            using var process = Process.Start(info);
            if (process is null)
                return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// <c>ja_JP.UTF-8@mod</c> taken apart. The order glibc writes is
    /// <c>language[_territory][.codeset][@modifier]</c>.
    /// </summary>
    public static (string Language, string Territory, string Codeset, string Modifier) Split(string name)
    {
        var modifier = string.Empty;
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            modifier = name[(at + 1)..];
            name = name[..at];
        }
        var codeset = string.Empty;
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            codeset = name[(dot + 1)..];
            name = name[..dot];
        }
        var underscore = name.IndexOf('_');
        return underscore >= 0
            ? (name[..underscore], name[(underscore + 1)..], codeset, modifier)
            : (name, string.Empty, codeset, modifier);
    }

    /// <summary>
    /// The .NET culture for a glibc locale: <c>sr_RS@latin</c> is <c>sr-Latn-RS</c>. Modifiers that
    /// are not a script are dropped here, so <c>ca_ES@valencia</c> reads as Catalan (Spain).
    /// </summary>
    public static string CultureName(string name)
    {
        var (language, territory, _, modifier) = Split(name);
        var parts = new[] { language, ScriptOf(modifier), territory }.Where(part => !string.IsNullOrEmpty(part));
        return string.Join('-', parts);
    }

    /// <summary>"Japanese (Japan)" in the UI language, or the locale's own name when ICU does not know it.</summary>
    public static string DisplayNameOf(string name)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(CultureName(name), predefinedOnly: true);
            var modifier = Split(name).Modifier;
            // A modifier that did not become a script still tells the entry from its sibling
            // (ca_ES and ca_ES@valencia), so it has to stay visible.
            return modifier.Length > 0 && ScriptOf(modifier) is null
                ? $"{culture.DisplayName} @{modifier}"
                : culture.DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return name;
        }
    }

    private static string? ScriptOf(string modifier) => modifier switch
    {
        "latin" => "Latn",
        "cyrillic" => "Cyrl",
        "devanagari" => "Deva",
        _ => null,
    };

    private static bool IsUtf8(string charset) => NormalizeCodeset(charset) == "utf8";

    private static string NormalizeCodeset(string codeset) =>
        new string([.. codeset.Where(char.IsLetterOrDigit)]).ToLowerInvariant();

    /// <summary>
    /// How <c>locale -a</c> names an entry: <c>ja_JP.utf8</c> for one generated as <c>ja_JP.UTF-8</c>,
    /// but plain <c>sr_RS@latin</c> for one whose name carries no codeset. So both sides are keyed
    /// as the base name plus the normalized codeset, empty when the name has none.
    /// </summary>
    private static string Key(string name)
    {
        var (language, territory, codeset, modifier) = Split(name);
        return $"{language}_{territory}@{modifier}|{NormalizeCodeset(codeset)}";
    }
}
