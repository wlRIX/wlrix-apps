using Wlrix.Settings.Login.Core;
using Wlrix.Settings.Outputs;

namespace Wlrix.Settings.Login.Services;

/// <summary>
/// Turning what the compositor says about a display into how greeter.toml spells it.
/// </summary>
internal static class OutputMapping
{
    /// <summary>
    /// A display exactly as it is now: what "Copy My Displays" writes for each one. Every field is
    /// set, so the login screen comes up as the session looks rather than as the compositor would
    /// choose by itself.
    /// </summary>
    public static GreeterOutput FromHead(OutputHead head) => new(
        head.Name,
        head.Enabled,
        head.CurrentMode is { } mode ? FromMode(mode) : null,
        head.X,
        head.Y,
        Math.Round(head.Scale, 3),
        TransformName(head.Transform));

    /// <summary>
    /// A protocol mode as the compositor's config spells it. Refresh rounds to whole hertz,
    /// which is what the compositor matches on: 59.94 and 60 are one choice.
    /// </summary>
    public static GreeterMode FromMode(OutputMode mode) =>
        new(mode.Width, mode.Height, (int)Math.Round(mode.RefreshMilliHz / 1000.0));

    /// <summary>The compositor's name for a transform (<c>format_transform</c> in <c>outputs.rs</c>).</summary>
    public static string TransformName(OutputTransform transform) => transform switch
    {
        OutputTransform.Rotate90 => "90",
        OutputTransform.Rotate180 => "180",
        OutputTransform.Rotate270 => "270",
        OutputTransform.Flipped => "flipped",
        OutputTransform.Flipped90 => "flipped-90",
        OutputTransform.Flipped180 => "flipped-180",
        OutputTransform.Flipped270 => "flipped-270",
        _ => "normal",
    };
}
