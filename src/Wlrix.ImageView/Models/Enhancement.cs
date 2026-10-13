// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ImageView.Models;

/// <summary>
/// The Enhance tab's settings: what is done to the pixels' values, as opposed to where they go.
/// </summary>
/// <remarks>
/// <para>Every setting is neutral at its default (all zero, saturation one) and the image is
/// left untouched there. Each one is a simple, linear operation, so their meaning can be read off
/// the formula:</para>
/// <list type="bullet">
/// <item><b>Sharpness</b> (−5 to 5): positive is an unsharp mask of that strength,
/// <c>c + s·(c − blur(c))</c>. Negative is a Gaussian blur with a radius (sigma) of that many pixels.</item>
/// <item><b>Haze</b> (−0.9 to 0.9): positive removes haze, taking the value as a black level and
/// stretching what is above it, <c>(c − h) / (1 − h)</c>. Negative adds haze, lifting everything
/// toward white, <c>c·(1 + h) − h</c>.</item>
/// <item><b>Brightness</b> (−1 to 1): added to every channel.</item>
/// <item><b>Contrast</b> (−2 to 2): the distance from mid-gray is scaled by <c>1 + k</c>.
/// At −1 everything is mid-gray, and below that the image is inverted.</item>
/// <item><b>Saturation</b> (−2 to 4): the distance from the pixel's own gray is scaled by
/// <c>σ</c>. At 0 the image is gray, negative values invert the hues and values above 1 saturate,
/// which is what the IRIX dialog's hint said.</item>
/// </list>
/// <para>Sharpness is applied first, and then the color operations in the order listed.
/// Together the color operations are one affine map per pixel, <see cref="ColorMatrix"/>.</para>
/// </remarks>
public readonly record struct Enhancement(double Sharpness, double Brightness, double Haze,
    double Contrast, double Saturation)
{
    public const double MinSharpness = -5, MaxSharpness = 5;
    public const double MinBrightness = -1, MaxBrightness = 1;
    public const double MinHaze = -0.9, MaxHaze = 0.9;
    public const double MinContrast = -2, MaxContrast = 2;
    public const double MinSaturation = -2, MaxSaturation = 4;

    /// <summary>Rec. 601 luma weights, as <see cref="Histogram"/> uses.</summary>
    private const double LumaRed = 0.299, LumaGreen = 0.587, LumaBlue = 0.114;

    public static Enhancement Identity { get; } = new(0, 0, 0, 0, 1);

    public bool IsIdentity => this == Identity;

    /// <summary>Whether anything is done to the colors, leaving sharpness aside.</summary>
    public bool ChangesColor => Brightness != 0 || Haze != 0 || Contrast != 0 || Saturation != 1;

    /// <summary>Clamps every setting to its range.</summary>
    public Enhancement Clamped() => new(
        Math.Clamp(Sharpness, MinSharpness, MaxSharpness),
        Math.Clamp(Brightness, MinBrightness, MaxBrightness),
        Math.Clamp(Haze, MinHaze, MaxHaze),
        Math.Clamp(Contrast, MinContrast, MaxContrast),
        Math.Clamp(Saturation, MinSaturation, MaxSaturation));

    /// <summary>
    /// The color operations as a 4×5 matrix in Skia's layout, row-major: <c>R' = m0·R + m1·G +
    /// m2·B + m3·A + m4</c>, and so on for G, B and A. Values are 0–1 and alpha is untouched.
    /// </summary>
    public float[] ColorMatrix()
    {
        // Each step as a 3×4 affine map on (R, G, B), composed in order.
        var map = Haze switch
        {
            > 0 => Affine.Uniform(1 / (1 - Haze), -Haze / (1 - Haze)),
            < 0 => Affine.Uniform(1 + Haze, -Haze),
            _ => Affine.Identity,
        };

        map = map.Then(Affine.Uniform(1, Brightness));
        map = map.Then(Affine.Uniform(1 + Contrast, -0.5 * Contrast));
        map = map.Then(Affine.Saturation(Saturation));

        var m = map.M;
        return
        [
            (float)m[0, 0], (float)m[0, 1], (float)m[0, 2], 0, (float)m[0, 3],
            (float)m[1, 0], (float)m[1, 1], (float)m[1, 2], 0, (float)m[1, 3],
            (float)m[2, 0], (float)m[2, 1], (float)m[2, 2], 0, (float)m[2, 3],
            0, 0, 0, 1, 0,
        ];
    }

    /// <summary>An affine map on RGB: a 3×3 matrix and an offset column.</summary>
    private readonly struct Affine(double[,] m)
    {
        public double[,] M { get; } = m;

        public static Affine Identity => Uniform(1, 0);

        /// <summary>Every channel scaled by <paramref name="scale"/>, then <paramref name="offset"/> added.</summary>
        public static Affine Uniform(double scale, double offset) => new(new double[,]
        {
            { scale, 0, 0, offset },
            { 0, scale, 0, offset },
            { 0, 0, scale, offset },
        });

        /// <summary>Each channel's distance from the pixel's luma scaled by <paramref name="s"/>.</summary>
        public static Affine Saturation(double s)
        {
            var g = new[] { LumaRed, LumaGreen, LumaBlue };
            var m = new double[3, 4];
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                m[row, column] = (1 - s) * g[column] + (row == column ? s : 0);
            return new Affine(m);
        }

        /// <summary>This map, then <paramref name="next"/>.</summary>
        public Affine Then(Affine next)
        {
            var a = next.M;
            var b = M;
            var result = new double[3, 4];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    var sum = column == 3 ? a[row, 3] : 0;
                    for (var k = 0; k < 3; k++)
                        sum += a[row, k] * b[k, column];
                    result[row, column] = sum;
                }
            }
            return new Affine(result);
        }
    }
}
