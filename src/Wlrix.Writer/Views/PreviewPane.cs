// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using ResvgSharp;
using Wlrix.Icons;
using Wlrix.Writer.Models;
using Wlrix.Writer.Services;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

/// <summary>The rendered side of a Markdown or SVG document, kept up to date as it is typed.</summary>
/// <remarks>
/// <para>
/// Rebuilt a moment after typing stops rather than on every keystroke: a Markdown render
/// replaces the whole control tree, and an SVG one rasterizes the whole drawing, and neither is
/// worth doing between the letters of a word.
/// </para>
/// <para>
/// The Markdown side is drawn in the session scheme's colors and redrawn when the scheme
/// changes, as the editor's highlighting is.
/// </para>
/// </remarks>
public sealed class PreviewPane : UserControl
{
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer;
    private readonly ScrollViewer _markdownScroll;
    private readonly Panel _svgPanel;
    private readonly Image _svgImage;
    private readonly TextBlock _svgError;
    private DocumentViewModel? _document;
    private TextDocument? _text;
    private PreviewKind _kind;
    private RenderedMarkdown? _rendered;
    private int _svgGeneration;
    private Size _svgRenderedFor;

    public PreviewPane()
    {
        _timer = new DispatcherTimer { Interval = TypingPause };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Render();
        };

        _markdownScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };

        _svgImage = new Image
        {
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _svgError = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8),
            IsVisible = false,
        };
        _svgPanel = new Panel { Children = { _svgImage, _svgError } };
        _svgPanel.SizeChanged += (_, _) => ScheduleSvg();

        ClipToBounds = true;
        ResourcesChanged += (_, _) =>
        {
            if (_kind == PreviewKind.Markdown)
                Schedule();
        };
    }

    /// <summary>Raised after the Markdown preview has been rebuilt, so a follower can scroll it again.</summary>
    public event Action? Rendered;

    /// <summary>The document being previewed, or null for none.</summary>
    public void Show(DocumentViewModel? document)
    {
        if (_text is not null)
            _text.TextChanged -= OnTextChanged;

        _document = document;
        _text = document?.Text;
        if (_text is not null)
            _text.TextChanged += OnTextChanged;

        Refresh();
    }

    /// <summary>Re-renders at once: the document changed kind, or the pane has just been shown.</summary>
    public void Refresh()
    {
        _timer.Stop();
        _kind = _document?.PreviewKind ?? PreviewKind.None;
        Content = _kind switch
        {
            PreviewKind.Markdown => _markdownScroll,
            PreviewKind.Svg => _svgPanel,
            _ => null,
        };
        _rendered = null;
        _svgRenderedFor = default;
        Render();
    }

    private void OnTextChanged(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    private void Render()
    {
        if (_text is null || !IsEffectivelyVisible && _rendered is not null)
            return;

        switch (_kind)
        {
            case PreviewKind.Markdown:
                RenderMarkdown(_text.Text);
                break;
            case PreviewKind.Svg:
                _svgRenderedFor = default;
                ScheduleSvg();
                break;
        }
    }

    // --- Markdown ------------------------------------------------------------------------------

    private void RenderMarkdown(string text)
    {
        var background = Brush("WlrixTextAreaBackground", Brushes.Gray);
        _markdownScroll.Background = background;

        var foreground = Brush("WlrixTextForeground", Brushes.Black);
        var palette = new MarkdownPalette(
            Foreground: foreground,
            Muted: Brush("WlrixDisabledForeground", Brushes.DimGray),
            Link: Brush("WlrixLink", Brushes.Blue),
            CodeBackground: Translucent(foreground, 0x1c),
            Rule: Brush("WlrixBottomShadow", Brushes.DimGray),
            Body: FontFamily,
            // The editor's own Courier chain, so code looks the same on both sides.
            Code: new FontFamily("Courier, Courier New, Nimbus Mono PS, Liberation Mono, DejaVu Sans Mono"),
            FontSize: 13);

        var directory = _document?.Path is { } path ? System.IO.Path.GetDirectoryName(path) : null;
        var renderer = new MarkdownRenderer(palette, directory, OpenLink,
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);

        // Kept where it was: a rebuild under the reader's eye should not throw them to the top.
        var offset = _markdownScroll.Offset;
        _rendered = renderer.Render(text);
        _markdownScroll.Content = _rendered.Root;
        Dispatcher.UIThread.Post(() =>
        {
            _markdownScroll.Offset = offset;
            Rendered?.Invoke();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Scrolls the Markdown preview to show what the editor has at its top: the block that
    /// <paramref name="line"/> falls in, part way down it by how far into it the line is.
    /// </summary>
    /// <param name="line">The zero-based line at the top of the editor.</param>
    /// <param name="atEnd">Whether the editor is scrolled to the bottom; the preview follows it there.</param>
    public void FollowEditor(int line, bool atEnd)
    {
        if (_kind != PreviewKind.Markdown || _rendered is not { } rendered
            || rendered.Root is not { } root || rendered.Blocks.Count == 0)
            return;

        var extent = _markdownScroll.Extent.Height - _markdownScroll.Viewport.Height;
        if (extent <= 0)
            return;
        if (atEnd)
        {
            _markdownScroll.Offset = new Vector(0, extent);
            return;
        }

        // The last block that starts at or before the line, and the one after it.
        var blocks = rendered.Blocks;
        var index = -1;
        for (var i = 0; i < blocks.Count && blocks[i].Line <= line; i++)
            index = i;
        if (index < 0)
        {
            _markdownScroll.Offset = default;
            return;
        }

        var (startLine, block) = blocks[index];
        if (block.TranslatePoint(default, root) is not { } top)
            return;

        var y = top.Y;
        var nextLine = index + 1 < blocks.Count ? blocks[index + 1].Line : startLine + 1;
        var nextY = index + 1 < blocks.Count && blocks[index + 1].Block.TranslatePoint(default, root) is { } next
            ? next.Y
            : y + block.Bounds.Height;
        if (nextLine > startLine)
            y += (nextY - y) * Math.Clamp((double)(line - startLine) / (nextLine - startLine), 0, 1);

        _markdownScroll.Offset = new Vector(0, Math.Clamp(y + root.Margin.Top - 10, 0, extent));
    }

    private async void OpenLink(string target)
    {
        // A link to a heading in this document scrolls to it.
        if (target.StartsWith('#'))
        {
            if (_rendered?.Anchors.TryGetValue(target[1..], out var heading) == true
                && heading.TranslatePoint(default, _rendered.Root) is { } point)
            {
                _markdownScroll.Offset = new Vector(0, point.Y);
            }
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
            return;

        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                await launcher.LaunchUriAsync(uri);
                return;
            }

            // A relative link names a file beside this one, which opens in whatever opens it.
            var directory = _document?.Path is { } path ? System.IO.Path.GetDirectoryName(path) : null;
            var local = uri?.IsFile == true ? uri.LocalPath
                : directory is null ? null
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(directory,
                    Uri.UnescapeDataString(target.Split('#', '?')[0])));
            if (local is not null && File.Exists(local))
                await launcher.LaunchFileInfoAsync(new FileInfo(local));
            else if (local is not null && Directory.Exists(local))
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(local));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or IOException)
        {
            // A link that will not open is a link that does nothing, not an error dialog.
        }
    }

    // --- SVG -----------------------------------------------------------------------------------

    private void ScheduleSvg() => Dispatcher.UIThread.Post(RenderSvg, DispatcherPriority.Background);

    /// <summary>
    /// Rasterizes the drawing to fit the pane, at the screen's scale, off the UI thread.
    /// </summary>
    /// <remarks>
    /// Fitted both ways, keeping its shape, so a 50pt IRIX icon fills the pane as large as it
    /// goes and a page-sized drawing shrinks to fit. A drawing that does not parse while it is
    /// being typed keeps the last good picture, with the reason underneath, rather than blanking.
    /// </remarks>
    private async void RenderSvg()
    {
        if (_kind != PreviewKind.Svg || _text is null)
            return;

        _svgPanel.Background = Brush("WlrixTextAreaBackground", Brushes.Gray);
        _svgError.Foreground = Brush("WlrixTextForeground", Brushes.Black);

        var available = _svgPanel.Bounds.Size.Deflate(new Thickness(16));
        if (available.Width < 1 || available.Height < 1 || available == _svgRenderedFor)
            return;
        _svgRenderedFor = available;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = (int)Math.Max(1, Math.Round(available.Width * scaling));
        var height = (int)Math.Max(1, Math.Round(available.Height * scaling));
        var svg = LegacySvg.WithNamespace(_text.Text);
        var directory = _document?.Path is { } path ? System.IO.Path.GetDirectoryName(path) : null;
        var generation = ++_svgGeneration;

        var (png, error) = await Task.Run<(byte[]? Png, string? Error)>(() =>
        {
            try
            {
                // resvg keeps the drawing's shape when given one dimension and stretches it when
                // given both. So: fit the width, and if that comes out too tall, fit the height.
                var fitted = Resvg.RenderToPng(svg, new ResvgOptions { Width = width, ResourcesDir = directory });
                if (PngHeight(fitted) > height)
                    fitted = Resvg.RenderToPng(svg, new ResvgOptions { Height = height, ResourcesDir = directory });
                return (fitted, null);
            }
            catch (ResvgSharp.Exceptions.ResvgException ex)
            {
                return (null, ex.Message);
            }
        });

        // A newer render was asked for while this one ran; its answer is the one to show.
        if (generation != _svgGeneration)
            return;

        _svgError.Text = error ?? "";
        _svgError.IsVisible = error is not null;
        if (png is null)
            return;

        using var stream = new MemoryStream(png);
        var bitmap = new Bitmap(stream);
        var old = _svgImage.Source as Bitmap;
        _svgImage.Source = bitmap;
        _svgImage.Width = bitmap.PixelSize.Width / scaling;
        _svgImage.Height = bitmap.PixelSize.Height / scaling;
        old?.Dispose();
    }

    /// <summary>The height a PNG declares in its header.</summary>
    private static int PngHeight(byte[] png) =>
        png.Length >= 24 ? (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23] : 0;

    // --- resources -----------------------------------------------------------------------------

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static IBrush Translucent(IBrush brush, byte alpha) =>
        brush is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, alpha / 255.0)
            : brush;
}
