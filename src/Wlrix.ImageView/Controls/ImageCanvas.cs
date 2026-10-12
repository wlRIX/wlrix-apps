// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wlrix.ImageView.Models;

namespace Wlrix.ImageView.Controls;

/// <summary>
/// The image well: the image drawn through a <see cref="ViewTransform"/> over a solid
/// background, and the left-button gestures that change the transform.
/// </summary>
/// <remarks>
/// <see cref="Transform"/> is written on every pointer move so the image follows the drag.
/// The undo history must only see whole gestures, so it listens to
/// <see cref="TransformCommitted"/>, raised once when the button is released.
/// </remarks>
public sealed class ImageCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageCanvas, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<ViewTransform> TransformProperty =
        AvaloniaProperty.Register<ImageCanvas, ViewTransform>(nameof(Transform),
            ViewTransform.Identity, defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<ViewTool> ToolProperty =
        AvaloniaProperty.Register<ImageCanvas, ViewTool>(nameof(Tool));

    /// <summary>What shows around the image. Black, as on IRIX imgview.</summary>
    /// <remarks>A property rather than a constant so a later release can let the user pick it.</remarks>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ImageCanvas>();

    /// <summary>
    /// How near the view center the pointer may come before rotation ignores it. Right at the
    /// center the angle swings wildly for a pixel of movement.
    /// </summary>
    private const double RotateDeadZone = 4;

    private Point _pressedAt;
    private ViewTransform _pressedTransform;
    private double _pressedAngle;
    private bool _dragging;

    static ImageCanvas()
    {
        AffectsRender<ImageCanvas>(SourceProperty, TransformProperty, BackgroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ImageCanvas>(true);
        FocusableProperty.OverrideDefaultValue<ImageCanvas>(true);
        // A default rather than a value set in the constructor, so a style or a binding can
        // still replace it.
        BackgroundProperty.OverrideDefaultValue<ImageCanvas>(Brushes.Black);
    }

    public ImageCanvas() => UpdateCursor();

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ViewTransform Transform
    {
        get => GetValue(TransformProperty);
        set => SetValue(TransformProperty, value);
    }

    public ViewTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Raised when a drag ends, with <see cref="Transform"/> where it left off.</summary>
    public event EventHandler? TransformCommitted;

    /// <summary>The matrix from image pixels to this control's coordinates.</summary>
    /// <remarks>
    /// The image center goes to the view center plus the pan. The image is scaled, then
    /// rotated, about its own center. Avalonia's matrices compose left to right, so this
    /// reads in the order the steps happen.
    /// </remarks>
    public static Matrix ImageToView(Size image, Size view, ViewTransform transform) =>
        Matrix.CreateTranslation(-image.Width / 2, -image.Height / 2)
        * Matrix.CreateScale(transform.Zoom, transform.Zoom)
        * Matrix.CreateRotation(transform.Rotation * Math.PI / 180)
        * Matrix.CreateTranslation(view.Width / 2 + transform.Pan.X, view.Height / 2 + transform.Pan.Y);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToolProperty)
            UpdateCursor();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        // Filled even when transparent, so the whole well is hit-testable and a drag can
        // start beside the image as well as on it.
        context.FillRectangle(Background ?? Brushes.Transparent, bounds);

        if (Source is not { } source)
            return;

        var transform = Transform;
        // Magnified, every image pixel is drawn as a crisp square, which is the reason to zoom
        // in on an image at all. Reduced, it is filtered so detail is averaged rather than
        // dropped.
        var interpolation = transform.Zoom >= 1
            ? BitmapInterpolationMode.None
            : BitmapInterpolationMode.HighQuality;

        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
        using (context.PushTransform(ImageToView(source.Size, bounds.Size, transform)))
            context.DrawImage(source, new Rect(source.Size));
    }

    private void UpdateCursor() => Cursor = new Cursor(Tool switch
    {
        ViewTool.Hand => StandardCursorType.Hand,
        ViewTool.Rotate => StandardCursorType.Cross,
        _ => StandardCursorType.SizeNorthSouth,
    });

    private Point ViewCenter => new(Bounds.Width / 2, Bounds.Height / 2);

    /// <summary>The vector from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Vector Between(Point from, Point to) => new(to.X - from.X, to.Y - from.Y);

    private static double AngleOf(Vector vector) => Math.Atan2(vector.Y, vector.X) * 180 / Math.PI;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (Source is null || !point.Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _pressedAt = point.Position;
        _pressedTransform = Transform;
        _pressedAngle = AngleOf(Between(ViewCenter, point.Position));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;

        var position = e.GetPosition(this);
        var delta = Between(_pressedAt, position);
        var start = _pressedTransform;

        switch (Tool)
        {
            case ViewTool.Hand:
                Transform = start.WithPan(start.Pan + delta);
                break;

            case ViewTool.Rotate:
            {
                var fromCenter = Between(ViewCenter, position);
                if (fromCenter.Length < RotateDeadZone)
                    break;

                var rotation = start.Rotation + AngleOf(fromCenter) - _pressedAngle;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    rotation = Math.Round(rotation / 90) * 90;

                // About the view center, not the image's: an image panned off to one side
                // swings around the middle of the window, so its offset turns with it.
                var turn = (rotation - start.Rotation) * Math.PI / 180;
                var pan = start.Pan;
                var turned = new Vector(
                    pan.X * Math.Cos(turn) - pan.Y * Math.Sin(turn),
                    pan.X * Math.Sin(turn) + pan.Y * Math.Cos(turn));
                Transform = start.WithRotation(rotation).WithPan(turned);
                break;
            }

            case ViewTool.Zoom:
            {
                // About the view center as well, so the pan scales by the same factor.
                var zoom = ViewTransform.ZoomForDrag(start.Zoom, delta.X, delta.Y);
                Transform = start.WithZoom(zoom).WithPan(start.Pan * (zoom / start.Zoom));
                break;
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging || e.InitialPressMouseButton != MouseButton.Left)
            return;

        e.Pointer.Capture(null);
        EndDrag();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // A drag cut short (the window lost the pointer, say) still moved the image. Leaving it
        // there uncommitted would make the next Undo jump back past it.
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        TransformCommitted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The zoom at which an image fits inside <paramref name="view"/>, never above 1. An image
    /// smaller than the window opens at its own size rather than blown up.
    /// </summary>
    public static double FitZoom(PixelSize image, Size view)
    {
        if (image.Width <= 0 || image.Height <= 0 || view.Width <= 0 || view.Height <= 0)
            return 1;
        return ViewTransform.ClampZoom(Math.Min(1, Math.Min(view.Width / image.Width, view.Height / image.Height)));
    }
}
