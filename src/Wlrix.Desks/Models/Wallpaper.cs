using Avalonia.Media;

namespace Wlrix.Desks.Models;

/// <summary>How a picture is fitted to a screen: <c>wlrix-bg</c>'s modes, under its names.</summary>
public enum WallpaperMode
{
    Tile,
    Center,
    Fit,
    Fill,
    Stretch,
    Solid,
}

/// <summary>
/// One screen's background, with every fallback already applied: what <c>wlrix-bg</c> paints
/// there, and what a desk's snapshot draws behind its windows.
/// </summary>
/// <param name="Image">The picture, or null for the color alone.</param>
public sealed record Wallpaper(string? Image, WallpaperMode Mode, Color Color)
{
    /// <summary><c>wlrix-bg</c>'s default for an unset mode: cover the screen, aspect kept.</summary>
    public const WallpaperMode DefaultMode = WallpaperMode.Fill;

    /// <summary><c>wlrix-bg</c>'s default for an unset color: the IRIX desktop gray.</summary>
    public static Color DefaultColor { get; } = Color.FromRgb(0x55, 0x55, 0x55);

    /// <summary>A plain gray desktop: what no configuration at all looks like.</summary>
    public static Wallpaper Default { get; } = new(null, DefaultMode, DefaultColor);

    /// <summary>
    /// A wallpaper from the three strings <c>background.toml</c> and the <c>wlrix-desks</c>
    /// background event both spell it with, or null when <c>wlrix-bg</c> would not draw it.
    /// </summary>
    /// <remarks>
    /// The picture is dropped under <see cref="WallpaperMode.Solid"/> and when the path is empty,
    /// which is how both sources say "the color alone".
    /// </remarks>
    public static Wallpaper? Parse(string image, string mode, string color)
    {
        if (ParseMode(mode) is not { } parsedMode || ParseColor(color) is not { } parsedColor)
            return null;
        return new Wallpaper(
            parsedMode == WallpaperMode.Solid || image.Length == 0 ? null : image, parsedMode, parsedColor);
    }

    /// <summary>A mode by its <c>background.toml</c> name; lowercase only, as serde reads it.</summary>
    public static WallpaperMode? ParseMode(string name) => name switch
    {
        "tile" => WallpaperMode.Tile,
        "center" => WallpaperMode.Center,
        "fit" => WallpaperMode.Fit,
        "fill" => WallpaperMode.Fill,
        "stretch" => WallpaperMode.Stretch,
        "solid" => WallpaperMode.Solid,
        _ => null,
    };

    /// <summary><c>#rrggbb</c> or <c>#rgb</c>, the <c>#</c> optional, as <c>wlrix-bg</c> takes it.</summary>
    public static Color? ParseColor(string text)
    {
        var digits = text.Trim().TrimStart('#');
        if (digits.Length is not (3 or 6) || !digits.All(Uri.IsHexDigit))
            return null;
        if (digits.Length == 3)
            digits = string.Concat(digits.Select(c => new string(c, 2)));
        var value = Convert.ToUInt32(digits, 16);
        return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
