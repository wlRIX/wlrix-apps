using SkiaSharp;

namespace Wlrix.Icons;

/// <summary>Which state an icon is in: at rest, under the pointer, or selected.</summary>
public enum IconState
{
    Normal,
    Hover,
    Selected,
    /// <summary>
    /// Selected, in a window without the keyboard. IRIX knocked the yellow back the way it
    /// knocked a resting icon back: the rest state's checkerboard with the selection yellow where
    /// white was -- <c>#ffff00</c> and <c>#c9c9c9</c>, measured pixel for pixel off an IRIX
    /// screen. So the selection keys would act on is the one drawn solid.
    /// </summary>
    SelectedInactive,
    /// <summary>
    /// What stays in place while the icon is dragged away: IRIX left a dark gray ghost there
    /// for as long as the drag lasted.
    /// </summary>
    Ghost,
    /// <summary>A folder that will take what is dragged over it, which IRIX turned royal blue.</summary>
    DropTarget,
}

/// <summary>
/// The colors an icon's body takes, as <c>0xRRGGBB</c>: the palette's <c>iconTint</c>,
/// <c>iconTintHover</c>, <c>iconTintSelected</c>, <c>iconTintGhost</c> and <c>iconTintDrop</c>.
/// </summary>
/// <remarks>
/// Values rather than a reference to the theme, so this assembly stays free of Avalonia. The
/// app reads them out of the scheme dictionary and hands them over, and again when the scheme
/// changes.
/// </remarks>
public readonly record struct IconStateColors(uint Rest, uint Hover, uint Selected, uint Ghost, uint Drop)
{
    /// <summary>The <c>classic</c> scheme's, for a caller with no theme to ask.</summary>
    public static IconStateColors Default { get; } = new(0xAAAAAA, 0xFFFFFF, 0xFFFF00, 0x555555, 0x0000FF);
}

/// <summary>
/// How an icon shows its state -- IRIX's rule, measured off a real IRIX screen rather than
/// guessed: <b>only the icon's light body changes.</b>
/// </summary>
/// <remarks>
/// A port of <c>wlrix-ui</c>'s <c>icon_state</c>, which the desktop's icons and the login
/// screen's user pictures follow, kept in step by hand: a selected file here and a selected
/// file on the desktop are the same yellow and change the same way.
///
/// <para>
/// At rest the body is a 50% checkerboard of white and the rest gray; under the pointer, solid
/// white; selected, solid lamp yellow. The outline, the drop shadow and colored detail -- a
/// folder's tan -- are drawn the same in all three. The body is found by lightness: a pixel
/// whose every channel is at least <see cref="BodyThreshold"/> is body, and is
/// <b>multiplied</b> by the state's color, so pure white takes the color outright and an
/// off-white body like a text file's <c>#ffefe5</c> keeps its shading.
/// </para>
///
/// <para>
/// A thumbnail -- a picture of the file itself -- is never recolored. It is framed instead,
/// as the login screen frames a user's own picture; see the icon view's template.
/// </para>
/// </remarks>
public static class IconStatePainter
{
    /// <summary>
    /// The lightest a pixel can be in every channel and still be drawing rather than body.
    /// </summary>
    /// <remarks>
    /// Between the darkest body color the wlrix icon theme uses (<c>#ffefe5</c>'s blue,
    /// <c>0xe5</c>) and the lightest non-body one (a <c>#999999</c> detail), with room for
    /// antialiasing on both sides. The same value as <c>wlrix-ui</c>'s.
    /// </remarks>
    public const byte BodyThreshold = 0xD0;

    /// <summary>The color the body takes at <c>(x, y)</c> of the icon, as <c>0xRRGGBB</c>.</summary>
    /// <remarks>
    /// At rest that alternates pixel by pixel between white and the rest gray, on a
    /// checkerboard anchored to the icon -- the stipple IRIX knocked an icon back with.
    /// </remarks>
    public static uint Body(IconState state, IconStateColors colors, int x, int y) => state switch
    {
        IconState.Normal when ((x + y) & 1) == 0 => 0xFFFFFF,
        IconState.Normal => colors.Rest,
        IconState.Hover => colors.Hover,
        IconState.SelectedInactive when ((x + y) & 1) == 0 => colors.Selected,
        IconState.SelectedInactive => colors.Rest,
        IconState.Ghost => colors.Ghost,
        IconState.DropTarget => colors.Drop,
        _ => colors.Selected,
    };

    /// <summary>One straight (not premultiplied) RGBA pixel of an icon at <c>(x, y)</c>, in <paramref name="state"/>.</summary>
    public static (byte R, byte G, byte B, byte A) Paint(
        IconState state, IconStateColors colors, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (a == 0 || Math.Min(r, Math.Min(g, b)) < BodyThreshold)
            return (r, g, b, a);
        var body = Body(state, colors, x, y);
        return (Multiply(r, (byte)(body >> 16)), Multiply(g, (byte)(body >> 8)), Multiply(b, (byte)body), a);
    }

    /// <summary>A rendered icon's PNG bytes, redrawn in <paramref name="state"/>.</summary>
    /// <returns>Null if the bytes do not decode.</returns>
    public static byte[]? Apply(byte[] png, IconState state, IconStateColors colors)
    {
        // Through a codec first: SKBitmap.Decode throws on bytes it has no codec for rather
        // than answering null, and one bad render should cost one icon, not the listing.
        using var codec = SKCodec.Create(new SKMemoryStream(png));
        if (codec is null)
            return null;
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
            return null;

        // Straight alpha, so the lightness test sees the color a pixel is rather than the color
        // scaled down by its coverage: an antialiased edge of a white body is still white.
        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var source = decoded.PeekPixels())
        using (var destination = bitmap.PeekPixels())
        {
            // ReadPixels converts to the destination's color and alpha type, which a plain
            // copy does not promise to.
            if (source is null || destination is null || !source.ReadPixels(destination))
                return null;
        }

        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var i = (y * bitmap.Width + x) * 4;
                var (r, g, b, a) = Paint(state, colors, x, y, pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = a;
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    private static byte Multiply(byte channel, byte by) => (byte)((channel * by + 127) / 255);
}
