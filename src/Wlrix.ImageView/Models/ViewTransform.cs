// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;

namespace Wlrix.ImageView.Models;

/// <summary>
/// How the image sits in the view: where its center is relative to the view's center, how far
/// it is turned, and how large it is drawn.
/// </summary>
/// <remarks>
/// Immutable, so the undo history can hold these by value and a drag in progress can never
/// reach back and change a step that was already committed.
///
/// Only <see cref="Rotation"/> survives a save. Pan and zoom are how the image is being looked
/// at, not something done to it.
/// </remarks>
/// <param name="Pan">The image center's offset from the view center, in view pixels.</param>
/// <param name="Rotation">Clockwise turn in degrees, normalized to [0, 360).</param>
/// <param name="Zoom">Scale from image pixels to view pixels.</param>
public readonly record struct ViewTransform(Vector Pan, double Rotation, double Zoom)
{
    public const double MinZoom = 1.0 / 32;
    public const double MaxZoom = 64;

    /// <summary>How close to a whole quarter turn still counts as one.</summary>
    /// <remarks>
    /// The rotation is accumulated from pointer angles, which are floating point; a snap to 90°
    /// can come back as 89.99999999. Anything this close is written as an exact quarter turn,
    /// which is the difference between a lossless save and a resampled one.
    /// </remarks>
    private const double QuarterTurnTolerance = 1e-6;

    public static ViewTransform Identity { get; } = new(default, 0, 1);

    public ViewTransform WithPan(Vector pan) => this with { Pan = pan };

    public ViewTransform WithRotation(double degrees) => this with { Rotation = NormalizeDegrees(degrees) };

    public ViewTransform WithZoom(double zoom) => this with { Zoom = ClampZoom(zoom) };

    /// <summary>Whether the image is turned at all, which is what a save would change.</summary>
    public bool IsRotated => QuarterTurns is not 0;

    /// <summary>
    /// The rotation as a whole number of quarter turns (0–3), or <c>null</c> for any other
    /// angle.
    /// </summary>
    public int? QuarterTurns
    {
        get
        {
            var turns = Rotation / 90;
            var nearest = Math.Round(turns);
            if (Math.Abs(turns - nearest) * 90 > QuarterTurnTolerance)
                return null;
            return (int)nearest % 4;
        }
    }

    public static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360;
        if (normalized < 0)
            normalized += 360;
        // 359.9999999 is a full turn that floating point did not quite finish.
        return 360 - normalized < QuarterTurnTolerance ? 0 : normalized;
    }

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>
    /// The zoom a drag by (<paramref name="dx"/>, <paramref name="dy"/>) view pixels makes from
    /// <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// Exponential, so every 120 pixels of travel doubles or halves the size wherever it starts:
    /// the same gesture feels the same at 4% as at 800%. Up and left both zoom in, as on IRIX,
    /// so a diagonal drag up and to the left counts twice.
    /// </remarks>
    public static double ZoomForDrag(double start, double dx, double dy) =>
        ClampZoom(start * Math.Pow(2, (-dx - dy) / 120));
}
