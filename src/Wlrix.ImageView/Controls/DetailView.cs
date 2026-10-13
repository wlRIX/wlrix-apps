// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Wlrix.ImageView.Controls;

/// <summary>
/// The Detail dialog's close-up: the image's own pixels around a point, each drawn as a square
/// <see cref="Magnification"/> view pixels on a side.
/// </summary>
/// <remarks>
/// In image space, not the main window's: the close-up is for reading pixels, so it is neither
/// turned nor scaled by the main view, and the magnification means the same thing however the
/// main window is zoomed.
/// </remarks>
public sealed class DetailView : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<DetailView, Bitmap?>(nameof(Source));

    /// <summary>The image point at the middle of the view, or <c>null</c> to show nothing.</summary>
    public static readonly StyledProperty<Point?> CenterProperty =
        AvaloniaProperty.Register<DetailView, Point?>(nameof(Center));

    public static readonly StyledProperty<double> MagnificationProperty =
        AvaloniaProperty.Register<DetailView, double>(nameof(Magnification), 2);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<DetailView>();

    static DetailView()
    {
        AffectsRender<DetailView>(SourceProperty, CenterProperty, MagnificationProperty, BackgroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<DetailView>(true);
        // The same black as the main view's well, so the image's edge reads the same in both.
        BackgroundProperty.OverrideDefaultValue<DetailView>(Brushes.Black);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Point? Center
    {
        get => GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public double Magnification
    {
        get => GetValue(MagnificationProperty);
        set => SetValue(MagnificationProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, bounds);

        if (Source is not { } source || Center is not { } center)
            return;

        var scale = Math.Max(1, Magnification);
        var middle = bounds.Center;
        var toView = Matrix.CreateTranslation(-center.X, -center.Y)
                     * Matrix.CreateScale(scale, scale)
                     * Matrix.CreateTranslation(middle.X, middle.Y);

        // Nearest-neighbor, always: blurring the pixels together is the opposite of what a
        // close-up is for.
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        using (context.PushTransform(toView))
            context.DrawImage(source, new Rect(source.Size));

        // A box around the pixel whose value the dialog reports, a pixel bigger than it all
        // round so it stays visible at 1X. Drawn twice, dark inside light, so it shows on any
        // color.
        var pixel = new Rect(Math.Floor(center.X), Math.Floor(center.Y), 1, 1).TransformToAABB(toView);
        var mark = pixel.Inflate(1.5);
        context.DrawRectangle(null, new Pen(Brushes.White, 1), mark);
        context.DrawRectangle(null, new Pen(Brushes.Black, 1), mark.Deflate(1));
    }
}
