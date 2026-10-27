// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ResvgSharp;
using Wlrix.Icons;

namespace Wlrix.Writer.Services;

/// <summary>The colors and fonts a Markdown preview is drawn in, read from the session scheme.</summary>
public sealed record MarkdownPalette(
    IBrush Foreground,
    IBrush Muted,
    IBrush Link,
    IBrush CodeBackground,
    IBrush Rule,
    FontFamily Body,
    FontFamily Code,
    double FontSize);

/// <summary>A rendered document: the control tree, and where each part of the source landed in it.</summary>
/// <param name="Root">The whole document, to put in a scroll viewer.</param>
/// <param name="Blocks">
/// Blocks in source order with the zero-based line each starts on: top-level blocks and list
/// items. What the preview scrolls by to follow the editor.
/// </param>
/// <param name="Anchors">Headings by their generated id, for <c>#section</c> links.</param>
public sealed record RenderedMarkdown(
    Control Root,
    IReadOnlyList<(int Line, Control Block)> Blocks,
    IReadOnlyDictionary<string, Control> Anchors);

/// <summary>Turns Markdown into Avalonia controls.</summary>
/// <remarks>
/// <para>
/// Markdig parses; the drawing is done here, into ordinary text blocks and panels, rather than
/// through HTML and a browser engine. That keeps the preview in the scheme's colors and fonts
/// like the rest of the window, and keeps a web engine out of a text editor.
/// </para>
/// <para>
/// Covers what READMEs and notes are written in: CommonMark plus pipe tables, task lists,
/// strikethrough and bare URLs. Raw HTML is not interpreted -- a block shows as its source,
/// dimmed, and inline tags are dropped except <c>&lt;br&gt;</c>. Images are drawn when they are
/// files beside the document; anything on the network shows its alt text instead, because
/// a preview fetching URLs while you type is a privacy leak nobody asked for.
/// </para>
/// </remarks>
public sealed class MarkdownRenderer
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseAutoIdentifiers()
        .UsePreciseSourceLocation()
        .Build();

    // Relative to the body size, roughly what a browser's default stylesheet uses.
    private static readonly double[] HeadingScale = [2.0, 1.6, 1.3, 1.15, 1.0, 0.9];

    private readonly MarkdownPalette _palette;
    private readonly string? _baseDirectory;
    private readonly Action<string> _openLink;
    private readonly double _scaling;
    private readonly List<(int Line, Control Block)> _blocks = [];
    private readonly Dictionary<string, Control> _anchors = new(StringComparer.Ordinal);

    /// <param name="baseDirectory">The document's folder, for relative image paths; null if untitled.</param>
    /// <param name="openLink">Called with a link's target when it is clicked.</param>
    /// <param name="scaling">The window's render scaling, so SVG images are rasterized sharp.</param>
    public MarkdownRenderer(MarkdownPalette palette, string? baseDirectory, Action<string> openLink,
        double scaling = 1.0)
    {
        _palette = palette;
        _baseDirectory = baseDirectory;
        _openLink = openLink;
        _scaling = scaling > 0 ? scaling : 1.0;
    }

    /// <summary>Parses <paramref name="markdown"/> and builds its controls.</summary>
    public RenderedMarkdown Render(string markdown)
    {
        _blocks.Clear();
        _anchors.Clear();

        var document = Markdown.Parse(markdown, Pipeline);
        var root = new StackPanel { Margin = new Thickness(14, 10, 14, 14) };
        foreach (var block in document)
        {
            // The block's place is taken before its children are rendered: a list's items add
            // themselves as they go, and the list has to come before them for the order to
            // stay the source's.
            var index = _blocks.Count;
            if (RenderBlock(block) is { } control)
            {
                _blocks.Insert(index, (block.Line, control));
                root.Children.Add(control);
            }
        }

        return new RenderedMarkdown(root, [.. _blocks], new Dictionary<string, Control>(_anchors));
    }

    // --- blocks --------------------------------------------------------------------------------

    private Control? RenderBlock(Block block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline, bottom: 10),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
        QuoteBlock quote => Quote(quote),
        ListBlock list => List(list),
        ThematicBreakBlock => new Separator { Margin = new Thickness(0, 6, 0, 12) },
        Table table => Table(table),
        HtmlBlock html => RawHtml(html),
        _ => null,
    };

    private Control Heading(HeadingBlock heading)
    {
        var level = Math.Clamp(heading.Level, 1, HeadingScale.Length);
        var text = Paragraph(heading.Inline, bottom: 0);
        text.FontSize = _palette.FontSize * HeadingScale[level - 1];
        text.FontWeight = FontWeight.Bold;

        Control control = text;
        // The two top levels get a rule under them, as GitHub draws it; here it is the scheme's
        // etched line, the same one menus and dialogs separate things with.
        if (level <= 2)
        {
            var panel = new StackPanel();
            panel.Children.Add(text);
            panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 0) });
            control = panel;
        }

        control.Margin = new Thickness(0, level <= 2 ? 10 : 8, 0, 8);
        if (heading.GetAttributes().Id is { Length: > 0 } id)
            _anchors.TryAdd(id, control);
        return control;
    }

    private SelectableTextBlock Paragraph(ContainerInline? inlines, double bottom)
    {
        var block = NewText();
        block.Margin = new Thickness(0, 0, 0, bottom);
        var links = new List<(int Start, int End, string Url)>();
        if (inlines is not null)
        {
            var position = 0;
            AddInlines(block.Inlines!, inlines, links, ref position);
        }

        if (links.Count > 0)
            AttachLinks(block, links);
        return block;
    }

    private Control Code(LeafBlock code)
    {
        var text = new SelectableTextBlock
        {
            Text = code.Lines.ToString(),
            FontFamily = _palette.Code,
            FontSize = _palette.FontSize,
            Foreground = _palette.Foreground,
            TextWrapping = TextWrapping.NoWrap,
        };

        // Long lines scroll sideways rather than wrap: wrapped code reads as different code.
        return new Border
        {
            Background = _palette.CodeBackground,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = text,
            },
        };
    }

    private Control Quote(QuoteBlock quote)
    {
        var panel = new StackPanel();
        foreach (var child in quote)
        {
            if (RenderBlock(child) is { } control)
                panel.Children.Add(control);
        }

        TrimLastMargin(panel);
        return new Border
        {
            BorderBrush = _palette.Rule,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel,
            // Dimmed as a whole; the quote's own links and code keep their colors under it.
            Opacity = 0.8,
        };
    }

    private Control List(ListBlock list)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        var row = 0;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // A task item's checkbox is its marker; a bullet beside it as well would be two.
            var isTask = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList };
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : isTask ? "" : "•",
                FontFamily = _palette.Body,
                FontSize = _palette.FontSize,
                Foreground = _palette.Foreground,
                Margin = new Thickness(4, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetRow(marker, row);
            grid.Children.Add(marker);

            var content = new StackPanel();
            foreach (var child in item)
            {
                if (RenderBlock(child) is not { } control)
                    continue;
                // A tight list's paragraphs sit on consecutive lines, as they were written.
                if (!list.IsLoose && child is ParagraphBlock)
                    control.Margin = new Thickness(0, 0, 0, 2);
                content.Children.Add(control);
            }

            TrimLastMargin(content, keep: list.IsLoose ? 6 : 2);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            _blocks.Add((item.Line, content));
            row++;
        }

        return grid;
    }

    private Control Table(Table table)
    {
        var columns = Math.Max(1, table.ColumnDefinitions.Count);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var row = 0;
        foreach (var tableRow in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var column = 0;
            foreach (var cell in tableRow.OfType<TableCell>())
            {
                var content = new StackPanel();
                foreach (var child in cell)
                {
                    if (RenderBlock(child) is { } control)
                    {
                        control.Margin = default;
                        if (tableRow.IsHeader && control is TextBlock text)
                            text.FontWeight = FontWeight.Bold;
                        content.Children.Add(control);
                    }
                }

                var index = cell.ColumnIndex >= 0 ? cell.ColumnIndex : column;
                var alignment = index < table.ColumnDefinitions.Count
                    ? table.ColumnDefinitions[index].Alignment
                    : null;
                content.HorizontalAlignment = alignment switch
                {
                    TableColumnAlign.Center => HorizontalAlignment.Center,
                    TableColumnAlign.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left,
                };

                // Each cell draws its right and bottom edges, the first row and column their
                // top and left, so neighbors never double a line.
                var border = new Border
                {
                    BorderBrush = _palette.Rule,
                    BorderThickness = new Thickness(index == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(8, 4),
                    Background = tableRow.IsHeader ? _palette.CodeBackground : null,
                    Child = content,
                };
                Grid.SetRow(border, row);
                Grid.SetColumn(border, Math.Min(index, columns - 1));
                Grid.SetColumnSpan(border, Math.Max(1, cell.ColumnSpan));
                grid.Children.Add(border);
                column = index + Math.Max(1, cell.ColumnSpan);
            }

            row++;
        }

        // Wide tables scroll sideways inside the preview rather than squeezing every column.
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private Control RawHtml(HtmlBlock html) => new SelectableTextBlock
    {
        Text = html.Lines.ToString(),
        FontFamily = _palette.Code,
        FontSize = _palette.FontSize,
        Foreground = _palette.Muted,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static void TrimLastMargin(StackPanel panel, double keep = 0)
    {
        if (panel.Children.Count > 0 && panel.Children[^1] is { } last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, keep);
    }

    private SelectableTextBlock NewText() => new()
    {
        FontFamily = _palette.Body,
        FontSize = _palette.FontSize,
        Foreground = _palette.Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    // --- inlines -------------------------------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="container"/>'s inlines, keeping count of the characters added so each
    /// link's span of the text is known for hit testing.
    /// </summary>
    /// <remarks>
    /// The count has to agree with how a text block flattens its inlines: a run is its text, a
    /// line break is <see cref="Environment.NewLine"/>, and an embedded control is one character.
    /// </remarks>
    private void AddInlines(InlineCollection target, ContainerInline container,
        List<(int Start, int End, string Url)> links, ref int position)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddRun(target, literal.Content.ToString(), ref position);
                    break;

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = _palette.Code,
                        Background = _palette.CodeBackground,
                    });
                    position += code.Content.Length;
                    break;

                case EmphasisInline emphasis:
                {
                    var span = emphasis switch
                    {
                        { DelimiterChar: '~' } => new Span { TextDecorations = TextDecorations.Strikethrough },
                        { DelimiterCount: >= 2 } => new Bold(),
                        _ => (Span)new Italic(),
                    };
                    AddInlines(span.Inlines, emphasis, links, ref position);
                    target.Add(span);
                    break;
                }

                case LinkInline { IsImage: true } image:
                    AddImage(target, image, ref position);
                    break;

                case LinkInline link:
                {
                    var start = position;
                    var span = LinkSpan();
                    AddInlines(span.Inlines, link, links, ref position);
                    target.Add(span);
                    if (link.Url is { Length: > 0 } url)
                        links.Add((start, position, url));
                    break;
                }

                case AutolinkInline auto:
                {
                    var start = position;
                    var span = LinkSpan();
                    AddRun(span.Inlines, auto.Url, ref position);
                    target.Add(span);
                    links.Add((start, position, auto.IsEmail ? "mailto:" + auto.Url : auto.Url));
                    break;
                }

                case TaskList task:
                    target.Add(new InlineUIContainer(new CheckBox
                    {
                        IsChecked = task.Checked,
                        IsHitTestVisible = false,
                        Focusable = false,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0, 0, 6, -3),
                    }));
                    position += 1;
                    break;

                case LineBreakInline lineBreak when lineBreak.IsHard:
                    target.Add(new LineBreak());
                    position += Environment.NewLine.Length;
                    break;

                case LineBreakInline:
                    // A soft break is a space: the paragraph rewraps to the preview's width.
                    AddRun(target, " ", ref position);
                    break;

                case HtmlEntityInline entity:
                    AddRun(target, entity.Transcoded.ToString(), ref position);
                    break;

                case HtmlInline html when IsBreakTag(html.Tag):
                    target.Add(new LineBreak());
                    position += Environment.NewLine.Length;
                    break;

                case HtmlInline:
                    // Other tags are dropped and their text kept, which is what a reader sees.
                    break;

                case ContainerInline nested:
                    AddInlines(target, nested, links, ref position);
                    break;
            }
        }
    }

    private static void AddRun(InlineCollection target, string text, ref int position)
    {
        target.Add(new Run(text));
        position += text.Length;
    }

    private Span LinkSpan() => new()
    {
        Foreground = _palette.Link,
        TextDecorations = TextDecorations.Underline,
    };

    private static bool IsBreakTag(string tag)
    {
        var name = tag.Trim('<', '>', '/', ' ').ToLowerInvariant();
        return name is "br" or "br/";
    }

    private void AddImage(InlineCollection target, LinkInline image, ref int position)
    {
        if (LoadImage(image.Url) is var (bitmap, size))
        {
            target.Add(new InlineUIContainer(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = size.Width,
                MaxHeight = size.Height,
            }));
            position += 1;
            return;
        }

        // What the image is of, where it would be: the alt text, dimmed and bracketed.
        var alt = new StringBuilder();
        for (var child = image.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is LiteralInline literal)
                alt.Append(literal.Content.ToString());
        }

        var text = $"[{(alt.Length > 0 ? alt.ToString() : image.Url)}]";
        target.Add(new Run(text) { Foreground = _palette.Muted });
        position += text.Length;
    }

    /// <summary>
    /// A local image, decoded, with the size it lays out at; null for one on the network or one
    /// that will not load.
    /// </summary>
    /// <remarks>
    /// An SVG is rasterized at the screen's scale and laid out at its own size, so it is sharp on
    /// a scaled display rather than a blown-up 1x render.
    /// </remarks>
    private (Bitmap Bitmap, Size Size)? LoadImage(string? url)
    {
        if (string.IsNullOrEmpty(url) || ResolveLocal(url) is not { } path || !File.Exists(path))
            return null;

        try
        {
            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var raster = new Bitmap(path);
                return (raster, raster.Size);
            }

            var png = Resvg.RenderToPng(LegacySvg.WithNamespace(File.ReadAllText(path)),
                new ResvgOptions { Zoom = (float)_scaling, ResourcesDir = Path.GetDirectoryName(path) });
            using var stream = new MemoryStream(png);
            var bitmap = new Bitmap(stream);
            return (bitmap, new Size(bitmap.PixelSize.Width / _scaling, bitmap.PixelSize.Height / _scaling));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or ResvgSharp.Exceptions.ResvgException)
        {
            return null;
        }
    }

    private string? ResolveLocal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.IsFile ? absolute.LocalPath : null;
        if (_baseDirectory is null)
            return null;
        var relative = Uri.UnescapeDataString(url.Split('#', '?')[0]);
        return Path.GetFullPath(Path.Combine(_baseDirectory, relative));
    }

    // --- links ---------------------------------------------------------------------------------

    /// <summary>
    /// Makes the link spans of <paramref name="block"/> clickable: a hand over them, and a click
    /// that is not the end of a text selection opens the target.
    /// </summary>
    private void AttachLinks(SelectableTextBlock block, List<(int Start, int End, string Url)> links)
    {
        string? LinkAt(Point point)
        {
            var hit = block.TextLayout.HitTestPoint(point - new Point(block.Padding.Left, block.Padding.Top));
            if (!hit.IsInside)
                return null;
            var index = hit.TextPosition;
            foreach (var (start, end, url) in links)
            {
                if (index >= start && index < end)
                    return url;
            }
            return null;
        }

        block.PointerMoved += (_, e) =>
            block.Cursor = LinkAt(e.GetPosition(block)) is null ? null : HandCursor;
        block.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || block.SelectionStart != block.SelectionEnd)
                return;
            if (LinkAt(e.GetPosition(block)) is { } url)
                _openLink(url);
        }, handledEventsToo: true);
    }
}
