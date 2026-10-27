using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Wayland;
using Wlrix.Archiver.ViewModels;

namespace Wlrix.Archiver.Views;

/// <summary>
/// Draws the picture that follows the pointer while entries are dragged out.
/// </summary>
/// <remarks>
/// The listing's own folder and file glyphs, drawn larger, with a count when there is more than
/// one. Without a drag image the compositor shows nothing but the cursor, which is how a drag
/// out of this window used to look: indistinguishable from no drag at all.
///
/// <para>
/// Rendered to pixels here because that is what crosses to the compositor: a shared-memory
/// buffer, with no renderer on the other side to hand a control to.
/// </para>
/// </remarks>
internal static class DragImageRenderer
{
    /// <summary>The glyphs' design size, the same 16 units the listing draws them at.</summary>
    private const int GlyphSize = 16;

    /// <summary>How many times larger than the listing the dragged glyph is drawn.</summary>
    private const int Magnify = 2;

    /// <summary>Room around it for the count badge to sit in without being clipped.</summary>
    private const int Padding = 6;

    // The same outlines as the TreeDataTemplate in MainWindow.axaml.
    private static readonly Geometry Folder =
        StreamGeometry.Parse("M1,4 L1,3 L6,3 L7,4.5 L15,4.5 L15,13 L1,13 Z");
    private static readonly Geometry File =
        StreamGeometry.Parse("M3,1 L10,1 L13,4 L13,15 L3,15 Z M10,1 L10,4 L13,4");

    /// <summary>The brushes the listing paints its glyphs with, resolved from the window.</summary>
    public readonly record struct Palette(IBrush FolderFill, IBrush FileFill, IBrush Outline);

    /// <summary>Draws <paramref name="rows"/>, or null if there is nothing worth drawing.</summary>
    public static WaylandDragImage? Render(IReadOnlyList<EntryNodeViewModel> rows, Palette palette,
        double scaling)
    {
        if (rows.Count == 0)
            return null;

        var scale = scaling > 0 ? scaling : 1.0;
        var side = GlyphSize * Magnify + Padding * 2;
        var pixels = new PixelSize(
            Math.Max(1, (int)Math.Round(side * scale)),
            Math.Max(1, (int)Math.Round(side * scale)));

        try
        {
            using var target = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            using (var context = target.CreateDrawingContext())
            {
                Draw(context, rows, palette, side);
            }
            return WaylandDragImage.FromBitmap(target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // A drag with no picture still extracts, so a renderer that cannot run is not worth
            // failing the gesture over.
            return null;
        }
    }

    private static void Draw(DrawingContext context, IReadOnlyList<EntryNodeViewModel> rows,
        Palette palette, int side)
    {
        var directory = rows[0].IsDirectory;
        using (context.PushTransform(Matrix.CreateScale(Magnify, Magnify)
                   * Matrix.CreateTranslation(Padding, Padding)))
        {
            // The outline scales with the glyph, so it keeps the listing's proportions.
            context.DrawGeometry(directory ? palette.FolderFill : palette.FileFill,
                new Pen(palette.Outline, 1), directory ? Folder : File);
        }

        if (rows.Count < 2)
            return;

        var text = new FormattedText(
            rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            12,
            Brushes.White);

        var badge = new Rect(
            side - text.Width - 10, side - text.Height - 6,
            text.Width + 8, text.Height + 2);
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
            new Pen(Brushes.White, 1),
            badge, 3, 3);
        context.DrawText(text, new Point(badge.X + 4, badge.Y + 1));
    }
}
