using System.Globalization;

namespace Wlrix.Settings.Login.Core;

/// <summary>
/// The login screen's settings: the parts of <c>/etc/wlrix/greeter.toml</c> this panel owns.
///
/// Every field is optional, and an absent one is left out of the file, so the compositor uses
/// its own default rather than a copy of it written down here. The file can hold more than this
/// (<c>[cursor]</c>, say), and <see cref="GreeterConfigFile.Apply"/> keeps whatever else is
/// there.
/// </summary>
public sealed record GreeterConfig
{
    /// <summary>A scheme id: <c>classic</c>, <c>classic-g10</c>, <c>classic-g24</c>, <c>gotham</c>.</summary>
    public string? Palette { get; init; }

    /// <summary>xkb rules, almost always left to the default (<c>evdev</c>).</summary>
    public string? Rules { get; init; }

    /// <summary>xkb model, e.g. <c>pc105</c> or <c>jp106</c>.</summary>
    public string? Model { get; init; }

    /// <summary>xkb layouts, comma separated, e.g. <c>jp,us</c>.</summary>
    public string? Layout { get; init; }

    /// <summary>xkb variants, one per layout, comma separated.</summary>
    public string? Variant { get; init; }

    /// <summary>xkb options, e.g. <c>grp:alt_shift_toggle</c>.</summary>
    public string? Options { get; init; }

    /// <summary>Milliseconds before a held key repeats.</summary>
    public int? RepeatDelay { get; init; }

    /// <summary>Repeats per second; 0 turns repeat off.</summary>
    public int? RepeatRate { get; init; }

    /// <summary>
    /// One entry per display the login screen has an opinion about. Empty means the compositor
    /// lays the displays out itself, which is what it does with no file at all.
    /// </summary>
    public IReadOnlyList<GreeterOutput> Outputs { get; init; } = [];

    /// <summary>Whether this says anything about the keyboard.</summary>
    public bool HasKeyboard => Rules is not null || Model is not null || Layout is not null
                               || Variant is not null || Options is not null
                               || RepeatDelay is not null || RepeatRate is not null;

    public bool Equals(GreeterConfig? other) =>
        other is not null
        && Palette == other.Palette && Rules == other.Rules && Model == other.Model
        && Layout == other.Layout && Variant == other.Variant && Options == other.Options
        && RepeatDelay == other.RepeatDelay && RepeatRate == other.RepeatRate
        && Outputs.SequenceEqual(other.Outputs);

    public override int GetHashCode() =>
        HashCode.Combine(Palette, Layout, Model, RepeatDelay, RepeatRate, Outputs.Count);
}

/// <summary>
/// One <c>[[output]]</c> entry, in the shape of the compositor's <c>OutputConfig</c>
/// (<c>wlrix-compositor/src/outputs.rs</c>). Null fields are left out, meaning "no opinion".
/// </summary>
/// <param name="Name">The connector, e.g. <c>DP-1</c>. The only thing the compositor matches on.</param>
/// <param name="Mode">Null for the display's preferred mode.</param>
/// <param name="Enabled">Null is on.</param>
public sealed record GreeterOutput(
    string Name,
    bool? Enabled = null,
    GreeterMode? Mode = null,
    int? X = null,
    int? Y = null,
    double? Scale = null,
    string? Transform = null)
{
    /// <summary>
    /// The orientations the compositor accepts, spelled as it writes them itself
    /// (<c>format_transform</c> in <c>outputs.rs</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> Transforms =
        ["normal", "90", "180", "270", "flipped", "flipped-90", "flipped-180", "flipped-270"];
}

/// <summary>
/// A resolution and refresh, as the compositor's config spells it: <c>1920x1080@60</c>, or
/// <c>1920x1080</c> for the fastest refresh at that size. Whole hertz, because that is what the
/// compositor parses and what it matches against.
/// </summary>
public sealed record GreeterMode(int Width, int Height, int? RefreshHz)
{
    public override string ToString() => RefreshHz is { } hz
        ? string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}@{hz}")
        : string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");

    /// <summary>
    /// Reads <c>WxH</c> or <c>WxH@Hz</c>. Hertz may have a fraction or a trailing <c>Hz</c>,
    /// as the compositor allows; it is rounded to whole hertz.
    /// </summary>
    public static GreeterMode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var (resolution, refresh) = text.Split('@', 2) is [var r, var hz] ? (r, hz) : (text, null);
        if (resolution.Split('x', 2) is not [var w, var h]
            || !int.TryParse(w.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(h.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            return null;

        if (refresh is null)
            return new GreeterMode(width, height, null);

        var number = refresh.Trim();
        if (number.EndsWith("Hz", StringComparison.OrdinalIgnoreCase))
            number = number[..^2].Trim();
        return double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? new GreeterMode(width, height, (int)Math.Round(value))
            : null;
    }
}
