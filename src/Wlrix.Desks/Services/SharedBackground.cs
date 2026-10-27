using Tomlyn;
using Tomlyn.Model;
using Wlrix.Desks.Models;

namespace Wlrix.Desks.Services;

/// <summary>
/// <c>background.toml</c>: the background every desk without one of its own shows, as
/// <c>wlrix-bg</c> reads it.
/// </summary>
/// <remarks>
/// The same rules as <c>wlrix-bg</c>'s <c>config.rs</c>, so a snapshot shows what the desktop
/// shows: the user's file, else <c>/etc/wlrix</c>'s, the first found winning outright; top-level
/// <c>image</c>, <c>mode</c> and <c>color</c>, overridden field by field per monitor by
/// <c>[[output]]</c> sections keyed by connector name; and <c>image = ""</c> in an override as
/// "no picture on this monitor".
///
/// <para>
/// Read leniently, unlike <c>wlrix-bg</c>, which refuses a whole file over one unknown key. That
/// refusal is <c>wlrix-bg</c>'s to report; a preview that disagreed would only be wrong about a
/// file the desktop is already showing as plain gray, and the field it could read is a better
/// guess at what was meant.
/// </para>
/// </remarks>
public sealed class SharedBackground
{
    private readonly Override _top;
    private readonly IReadOnlyList<Override> _outputs;

    private sealed record Override(string? Name, string? Image, WallpaperMode? Mode, global::Avalonia.Media.Color? Color);

    private SharedBackground(Override top, IReadOnlyList<Override> outputs)
    {
        _top = top;
        _outputs = outputs;
    }

    /// <summary>No file at all: a plain gray desktop.</summary>
    public static SharedBackground Empty { get; } = new(new Override(null, null, null, null), []);

    /// <summary>What <paramref name="output"/> shows; null for a monitor with no name.</summary>
    public Wallpaper Resolve(string? output)
    {
        var over = output is null ? null : _outputs.FirstOrDefault(entry => entry.Name == output);
        var mode = over?.Mode ?? _top.Mode ?? Wallpaper.DefaultMode;
        var color = over?.Color ?? _top.Color ?? Wallpaper.DefaultColor;
        var image = over?.Image ?? _top.Image;
        if (mode == WallpaperMode.Solid || string.IsNullOrEmpty(image))
            image = null;
        return new Wallpaper(image, mode, color);
    }

    /// <summary>The files <c>wlrix-bg</c> looks at, in the order it looks.</summary>
    public static IEnumerable<string> SearchPaths()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        yield return Path.Combine(configHome, "wlrix", "background.toml");
        yield return "/etc/wlrix/background.toml";
    }

    /// <summary>Reads the first file there is. Never throws: anything unreadable is the default.</summary>
    public static SharedBackground Load()
    {
        foreach (var path in SearchPaths())
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            return Parse(text);
        }
        return Empty;
    }

    /// <summary>Parses the file's text; what cannot be read is left to the defaults.</summary>
    public static SharedBackground Parse(string text)
    {
        TomlTable document;
        try
        {
            document = TomlSerializer.Deserialize<TomlTable>(text) ?? [];
        }
        catch (TomlException)
        {
            // wlrix-bg falls back to the defaults for a file that does not parse, and so does this.
            return Empty;
        }

        var outputs = new List<Override>();
        if (document.TryGetValue("output", out var raw) && raw is TomlTableArray sections)
        {
            foreach (var section in sections)
            {
                if (section.TryGetValue("name", out var name) && name is string text2)
                    outputs.Add(Read(section) with { Name = text2 });
            }
        }
        return new SharedBackground(Read(document), outputs);
    }

    private static Override Read(TomlTable table) => new(
        null,
        table.TryGetValue("image", out var image) ? image as string : null,
        table.TryGetValue("mode", out var mode) && mode is string modeText ? Wallpaper.ParseMode(modeText) : null,
        table.TryGetValue("color", out var color) && color is string colorText ? Wallpaper.ParseColor(colorText) : null);
}
