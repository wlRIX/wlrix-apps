namespace Wlrix.Files.Core.Dnd;

/// <summary>
/// How fast a listing should scroll when a drag is held near its edge.
/// </summary>
/// <remarks>
/// Without this, dragging a file into a folder that is off the bottom of a long directory is
/// impossible: the pointer button is already down, so the wheel is the only way to scroll and
/// one hand cannot do both.
///
/// <para>
/// The rate ramps rather than switching on, because a fixed speed is either too slow to reach
/// the end of a large directory or too fast to stop on the folder you wanted. Ramped, the edge
/// of the margin creeps and the very edge races, and the pointer position chooses.
/// </para>
/// </remarks>
public static class EdgeScroll
{
    /// <summary>How deep the sensitive band at each edge is, in pixels.</summary>
    public const double DefaultMargin = 28;

    /// <summary>The rate at the very edge, in pixels per second.</summary>
    public const double DefaultMaxRate = 900;

    /// <summary>
    /// The scroll rate for a pointer at <paramref name="position"/> in a viewport of
    /// <paramref name="extent"/> pixels: negative towards the start, positive towards the
    /// end, zero in the middle.
    /// </summary>
    /// <remarks>
    /// A pointer outside the viewport entirely still scrolls, at the full rate. That is not
    /// an edge case during a drag — it is what happens the moment the user overshoots, and
    /// stopping dead there would feel broken.
    /// </remarks>
    public static double Rate(
        double position,
        double extent,
        double margin = DefaultMargin,
        double maxRate = DefaultMaxRate)
    {
        if (extent <= 0 || margin <= 0 || maxRate <= 0)
            return 0;

        // A viewport shorter than two margins would have overlapping bands, where a pointer
        // in the middle belongs to both and the two answers cancel. Halving keeps them
        // adjacent instead, so a short listing still scrolls both ways.
        var band = Math.Min(margin, extent / 2);

        if (position < band)
            return -maxRate * Ramp((band - position) / band);
        if (position > extent - band)
            return maxRate * Ramp((position - (extent - band)) / band);
        return 0;
    }

    /// <summary>How far to scroll over <paramref name="elapsed"/> at <paramref name="rate"/>.</summary>
    public static double Delta(double rate, TimeSpan elapsed) =>
        rate * Math.Max(0, elapsed.TotalSeconds);

    /// <summary>Clamps the 0-to-1 depth into the band, so overshooting does not overspeed.</summary>
    private static double Ramp(double depth) => Math.Clamp(depth, 0, 1);
}
