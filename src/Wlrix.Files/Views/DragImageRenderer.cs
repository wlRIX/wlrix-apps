using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Wayland;
using Wlrix.Files.ViewModels;

namespace Wlrix.Files.Views;

/// <summary>
/// Draws the picture that follows the pointer during a drag.
/// </summary>
/// <remarks>
/// IRIX's fm dragged the icon itself, and that is what this reproduces: the file's own icon
/// under the cursor, with a count when there is more than one. Without it a drag is a cursor
/// shape and nothing else, and the cursor cannot say <i>what</i> is being carried.
///
/// <para>
/// Rendered here rather than handed over as a control, because what crosses to the compositor
/// is a shared-memory buffer of pixels: the backend has no renderer of its own and no decoder,
/// so somebody has to have already turned this into bytes.
/// </para>
/// </remarks>
internal static class DragImageRenderer
{
    /// <summary>How big the dragged icon is drawn.</summary>
    private const int IconSize = 48;

    /// <summary>Room around it for the count badge to sit in without being clipped.</summary>
    private const int Padding = 6;

    /// <summary>
    /// Draws the rows being dragged, or null if there is nothing worth drawing.
    /// </summary>
    /// <remarks>
    /// One icon whatever the count, with a badge for the rest. A stack of overlapping icons
    /// would say more but costs a render per item, and the count is what the user actually
    /// needs to know when they are about to drop forty files somewhere.
    /// </remarks>
    public static WaylandDragImage? Render(IReadOnlyList<FileEntryViewModel> rows, double scaling)
    {
        if (rows.Count == 0)
            return null;

        var scale = scaling > 0 ? scaling : 1.0;
        var side = IconSize + Padding * 2;
        var pixels = new PixelSize(
            Math.Max(1, (int)Math.Round(side * scale)),
            Math.Max(1, (int)Math.Round(side * scale)));

        try
        {
            using var target = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            using (var context = target.CreateDrawingContext())
            {
                Draw(context, rows, side);
            }
            return WaylandDragImage.FromBitmap(target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // A drag with no picture is the behavior we had before and is still a working
            // drag, so a renderer that cannot run is not worth failing the gesture over.
            return null;
        }
    }

    private static void Draw(DrawingContext context, IReadOnlyList<FileEntryViewModel> rows, int side)
    {
        var icon = rows[0].Preview;
        if (icon is not null)
        {
            var box = new Rect(Padding, Padding, IconSize, IconSize);
            // Fitted rather than stretched: a thumbnail is whatever shape the photograph is,
            // and squashing it into a square would misrepresent what is being dragged.
            var scale = Math.Min(IconSize / icon.Size.Width, IconSize / icon.Size.Height);
            var width = icon.Size.Width * scale;
            var height = icon.Size.Height * scale;
            context.DrawImage(icon, new Rect(
                box.X + (IconSize - width) / 2,
                box.Y + (IconSize - height) / 2,
                width, height));
        }
        else
        {
            // No icon rendered yet. A plain outline still says "something is being carried",
            // which is the whole job.
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(160, 220, 220, 220)),
                new Pen(new SolidColorBrush(Color.FromArgb(220, 60, 60, 60))),
                new Rect(Padding + 8, Padding + 4, IconSize - 16, IconSize - 8));
        }

        if (rows.Count < 2)
            return;

        // The count, bottom right. What matters when you are about to drop forty files.
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
