namespace Wlrix.Settings.Displays.Models;

/// <summary>
/// How a display's picture is turned, in <c>wl_output.transform</c>'s numbering, so a value
/// passes straight to and from the protocol. The panel offers only the four rotations; a flipped
/// transform somebody set with another tool is kept as it is unless a rotation is chosen.
/// </summary>
public enum OutputTransform
{
    Normal = 0,
    Rotate90 = 1,
    Rotate180 = 2,
    Rotate270 = 3,
    Flipped = 4,
    Flipped90 = 5,
    Flipped180 = 6,
    Flipped270 = 7,
}

/// <summary>One mode a display offers. Refresh is in mHz, as the protocol gives it.</summary>
public sealed record OutputMode(int Width, int Height, int RefreshMilliHz, bool Preferred)
{
    /// <summary>Whether this is the same size and refresh, ignoring whether it is preferred.</summary>
    public bool SameAs(OutputMode? other) =>
        other is not null && Width == other.Width && Height == other.Height
        && RefreshMilliHz == other.RefreshMilliHz;
}

/// <summary>
/// One display as the compositor last described it: a <c>zwlr_output_head_v1</c> together with
/// its <c>wlrix_output_color_head_v1</c> extension.
/// </summary>
/// <param name="Name">The connector name, e.g. <c>DP-4</c>. What the compositor keys on.</param>
/// <param name="CurrentMode">Null for a display that is switched off.</param>
/// <param name="SdrWhiteNits">The luminance SDR white is shown at while the display is in HDR.</param>
public sealed record OutputHead(
    string Name,
    string Make,
    string Model,
    string? Serial,
    string Description,
    int PhysicalWidthMm,
    int PhysicalHeightMm,
    IReadOnlyList<OutputMode> Modes,
    OutputMode? CurrentMode,
    bool Enabled,
    int X,
    int Y,
    OutputTransform Transform,
    double Scale,
    bool AdaptiveSync,
    bool AdaptiveSyncSupported,
    bool HdrSupported,
    bool HdrEnabled,
    int SdrWhiteNits);

/// <summary>Every display, as of one <c>done</c> from the compositor.</summary>
public sealed record OutputSnapshot(IReadOnlyList<OutputHead> Heads);

/// <summary>
/// What one display should be, as the panel asks for it. A whole description rather than a
/// difference: the feed works out what actually needs sending.
/// </summary>
public sealed record OutputChange(
    string Name,
    bool Enabled,
    OutputMode? Mode,
    int X,
    int Y,
    OutputTransform Transform,
    double Scale,
    bool AdaptiveSync,
    bool Hdr,
    int SdrWhiteNits)
{
    /// <summary>A change that leaves <paramref name="head"/> exactly as it is.</summary>
    public static OutputChange From(OutputHead head) => new(
        head.Name, head.Enabled, head.CurrentMode, head.X, head.Y, head.Transform, head.Scale,
        head.AdaptiveSync, head.HdrEnabled, head.SdrWhiteNits);
}

/// <summary>What became of a configuration sent to the compositor.</summary>
public enum ApplyResult
{
    /// <summary>Applied.</summary>
    Succeeded,

    /// <summary>The compositor refused it as a whole; nothing changed.</summary>
    Failed,

    /// <summary>
    /// The displays changed while it was on its way (one was plugged in, say), so it described
    /// a layout that no longer exists. Nothing changed.
    /// </summary>
    Cancelled,

    /// <summary>There is no compositor to send it to.</summary>
    Unavailable,
}
