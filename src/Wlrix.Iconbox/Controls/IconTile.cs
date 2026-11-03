using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Wlrix.Iconbox.Controls;

/// <summary>
/// One window in the iconbox: the compositor's 4Dwm icon tile while the window is minimized,
/// and the same picture and label drawn flat while it is up.
/// </summary>
/// <remarks>
/// The geometry is the compositor's, from <c>wlrix-compositor/src/decoration.rs</c>, so a window
/// looks the same here as on the desktop with the iconbox off: a 97x99 panel raised two pixels
/// in the inactive frame's shadows, an 85x67 picture six in from the corner in a sunken well, a
/// two-row groove under it, and the title centered below. Drawn by hand rather than composed of
/// bevel controls, because it has to land on the same pixels as the compositor's quads.
/// </remarks>
public sealed class IconTile : Control
{
    public const double TileWidth = 97;
    public const double TileHeight = 99;
    private const double Bevel = 2;
    private const double PictureInset = 6;
    private const double PictureWidth = 85;
    private const double PictureHeight = 67;
    private const double GrooveY = PictureInset + PictureHeight + 5;
    private const double GrooveHeight = 2;
    private const double LabelSize = 13;
    // The keyboard-focus outline round an open window's tile.
    private const double OutlineThickness = 2;

    // Decoded once per file for the life of the process: a few dozen small PNGs at most, and a
    // tile is redrawn on every state change of its window.
    private static readonly Dictionary<string, Bitmap?> Bitmaps = new(StringComparer.Ordinal);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<IconTile, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string?> ImagePathProperty =
        AvaloniaProperty.Register<IconTile, string?>(nameof(ImagePath));

    public static readonly StyledProperty<bool> MinimizedProperty =
        AvaloniaProperty.Register<IconTile, bool>(nameof(Minimized));

    public static readonly StyledProperty<bool> ActivatedProperty =
        AvaloniaProperty.Register<IconTile, bool>(nameof(Activated));

    public static readonly StyledProperty<Color> FaceColorProperty =
        AvaloniaProperty.Register<IconTile, Color>(nameof(FaceColor), Colors.DarkGray);

    public static readonly StyledProperty<Color> WellColorProperty =
        AvaloniaProperty.Register<IconTile, Color>(nameof(WellColor), Colors.DimGray);

    public static readonly StyledProperty<Color> LightColorProperty =
        AvaloniaProperty.Register<IconTile, Color>(nameof(LightColor), Colors.LightGray);

    public static readonly StyledProperty<Color> DarkColorProperty =
        AvaloniaProperty.Register<IconTile, Color>(nameof(DarkColor), Colors.Gray);

    public static readonly StyledProperty<Color> ForegroundColorProperty =
        AvaloniaProperty.Register<IconTile, Color>(nameof(ForegroundColor), Colors.Black);

    static IconTile()
    {
        AffectsRender<IconTile>(
            TitleProperty, ImagePathProperty, MinimizedProperty, ActivatedProperty,
            FaceColorProperty, WellColorProperty, LightColorProperty, DarkColorProperty,
            ForegroundColorProperty, TextElement.FontFamilyProperty);
    }

    /// <summary>The window's title.</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The picture file, from <see cref="Services.TileArt"/>.</summary>
    public string? ImagePath
    {
        get => GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    /// <summary>Whether to draw the raised tile, rather than the flat picture.</summary>
    public bool Minimized
    {
        get => GetValue(MinimizedProperty);
        set => SetValue(MinimizedProperty, value);
    }

    /// <summary>Whether to outline the tile, for the window with the keyboard.</summary>
    public bool Activated
    {
        get => GetValue(ActivatedProperty);
        set => SetValue(ActivatedProperty, value);
    }

    /// <summary>The tile's face: the scheme's <c>iconTileFace</c>.</summary>
    public Color FaceColor
    {
        get => GetValue(FaceColorProperty);
        set => SetValue(FaceColorProperty, value);
    }

    /// <summary>Behind the picture: the scheme's <c>iconWell</c>.</summary>
    public Color WellColor
    {
        get => GetValue(WellColorProperty);
        set => SetValue(WellColorProperty, value);
    }

    /// <summary>The lit edges: the inactive frame's top shadow.</summary>
    public Color LightColor
    {
        get => GetValue(LightColorProperty);
        set => SetValue(LightColorProperty, value);
    }

    /// <summary>The shaded edges: the inactive frame's bottom shadow.</summary>
    public Color DarkColor
    {
        get => GetValue(DarkColorProperty);
        set => SetValue(DarkColorProperty, value);
    }

    /// <summary>The label's color.</summary>
    public Color ForegroundColor
    {
        get => GetValue(ForegroundColorProperty);
        set => SetValue(ForegroundColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(TileWidth, TileHeight);

    public override void Render(DrawingContext context)
    {
        var tile = new Rect(0, 0, TileWidth, TileHeight);
        var picture = new Rect(PictureInset, PictureInset, PictureWidth, PictureHeight);

        // Something under every pixel, so a click between a flat tile's picture and its label
        // still lands on the tile.
        context.FillRectangle(Brushes.Transparent, tile);

        if (Minimized)
        {
            // Back to front, the reverse of the compositor's front-to-back list: the raised
            // panel, the sunken well, the groove, then the picture and label over them.
            DrawBevel(context, tile, FaceColor, raised: true);
            DrawBevel(context, picture.Inflate(Bevel), WellColor, raised: false);
            context.FillRectangle(new SolidColorBrush(DarkColor),
                new Rect(Bevel, GrooveY, TileWidth - 2 * Bevel, 1));
            context.FillRectangle(new SolidColorBrush(LightColor),
                new Rect(Bevel, GrooveY + 1, TileWidth - 2 * Bevel, 1));
        }

        DrawPicture(context, picture);

        var labelTop = GrooveY + GrooveHeight;
        DrawLabel(context, new Rect(Bevel, labelTop, TileWidth - 2 * Bevel, TileHeight - Bevel - labelTop));

        if (Activated && !Minimized)
        {
            var half = OutlineThickness / 2;
            context.DrawRectangle(new Pen(Brushes.White, OutlineThickness), tile.Deflate(half));
        }
    }

    /// <summary>
    /// A face with a bevel round it, cut the compositor's way (<c>Run::Vertical</c> in
    /// <c>wlrix-ui</c>): top and bottom run the full width, left and right fit between them.
    /// </summary>
    private void DrawBevel(DrawingContext context, Rect r, Color face, bool raised)
    {
        var topLeft = new SolidColorBrush(raised ? LightColor : DarkColor);
        var bottomRight = new SolidColorBrush(raised ? DarkColor : LightColor);
        context.FillRectangle(new SolidColorBrush(face), r.Deflate(Bevel));
        context.FillRectangle(topLeft, new Rect(r.X, r.Y, r.Width, Bevel));
        context.FillRectangle(topLeft, new Rect(r.X, r.Y + Bevel, Bevel, r.Height - 2 * Bevel));
        context.FillRectangle(bottomRight, new Rect(r.X, r.Bottom - Bevel, r.Width, Bevel));
        context.FillRectangle(bottomRight, new Rect(r.Right - Bevel, r.Y + Bevel, Bevel, r.Height - 2 * Bevel));
    }

    /// <summary>
    /// The picture, scaled to cover the area and cropped to it -- the compositor's
    /// <c>load_cover</c>, so off-aspect artwork is cut the same way in both places.
    /// </summary>
    private void DrawPicture(DrawingContext context, Rect area)
    {
        if (Load(ImagePath) is not { } bitmap)
            return;
        var size = bitmap.Size;
        var scale = Math.Max(area.Width / size.Width, area.Height / size.Height);
        var source = new Rect(
            (size.Width - area.Width / scale) / 2,
            (size.Height - area.Height / scale) / 2,
            area.Width / scale,
            area.Height / scale);
        context.DrawImage(bitmap, source, area);
    }

    private void DrawLabel(DrawingContext context, Rect area)
    {
        if (string.IsNullOrEmpty(Title) || area.Width <= 0)
            return;
        var text = new FormattedText(
            Title,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(GetValue(TextElement.FontFamilyProperty)),
            LabelSize,
            new SolidColorBrush(ForegroundColor))
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.None,
        };
        // Centered, and a title too long for the tile is cut off at its right edge rather than
        // ellipsized, like the compositor's label.
        var origin = new Point(
            area.X + Math.Max(0, area.Width - text.Width) / 2,
            area.Y + (area.Height - text.Height) / 2);
        using (context.PushClip(area))
            context.DrawText(text, origin);
    }

    private static Bitmap? Load(string? path)
    {
        if (path is null)
            return null;
        if (Bitmaps.TryGetValue(path, out var cached))
            return cached;
        Bitmap? bitmap;
        try
        {
            bitmap = new Bitmap(path);
        }
        catch (Exception)
        {
            // A file that is not a PNG after all: no picture, as with nothing installed.
            bitmap = null;
        }
        Bitmaps[path] = bitmap;
        return bitmap;
    }
}
