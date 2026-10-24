using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wlrix.Icons;

namespace Wlrix.Theme;

/// <summary>
/// An IRIX picture drawn from its SVG: a control panel's banner, the Shut Down System dialog's
/// task picture.
/// </summary>
/// <remarks>
/// Rasterized from the drawing at the window's own scale, and again whenever that changes, so
/// it is sharp on a 1.5x output where a PNG made once would be blurred. The drawings are IRIX
/// exports, which <see cref="LegacySvg"/> knows how to read. The size drawn at is the control's
/// <see cref="Layoutable.Width"/> and <see cref="Layoutable.Height"/>, which a banner sets.
///
/// <para>
/// A picture that cannot be read leaves the control empty: it is decoration, and no reason for a
/// window not to open.
/// </para>
/// </remarks>
public class IrixPicture : Image
{
    /// <summary>The drawing, as an <c>avares://</c> asset of the application.</summary>
    public static readonly StyledProperty<Uri?> AssetProperty =
        AvaloniaProperty.Register<IrixPicture, Uri?>(nameof(Asset));

    private TopLevel? _topLevel;

    public Uri? Asset
    {
        get => GetValue(AssetProperty);
        set => SetValue(AssetProperty, value);
    }

    // Image's own theme, not one of this type's: this is an Image that fills itself in.
    protected override Type StyleKeyOverride => typeof(Image);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
            _topLevel.ScalingChanged += OnScalingChanged;
        Draw();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
            _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AssetProperty || change.Property == WidthProperty || change.Property == HeightProperty)
            Draw();
    }

    private void OnScalingChanged(object? sender, EventArgs e) => Draw();

    private void Draw()
    {
        // wlRIX is Linux-only, and so is the icon library; the check is what lets this assembly
        // stay unmarked, rather than marking every application that follows the session scheme.
        if (!OperatingSystem.IsLinux() || Asset is not { } asset || _topLevel is not { } topLevel)
            return;

        var logical = Math.Max(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);
        if (logical <= 0)
            logical = 48;
        var pixels = (int)Math.Ceiling(logical * topLevel.RenderScaling);

        string svg;
        try
        {
            using var stream = AssetLoader.Open(asset);
            using var reader = new StreamReader(stream);
            svg = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException)
        {
            return;
        }

        if (LegacySvg.Render(svg, pixels) is not { } png)
            return;
        using var memory = new MemoryStream(png);
        var previous = Source as Bitmap;
        Source = new Bitmap(memory);
        previous?.Dispose();
    }
}
