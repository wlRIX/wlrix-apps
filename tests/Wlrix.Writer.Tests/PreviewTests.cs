// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using Wlrix.Writer.Models;
using Wlrix.Writer.Services;
using Wlrix.Writer.ViewModels;
using Wlrix.Writer.Views;
using Xunit;

namespace Wlrix.Writer.Tests;

/// <summary>The Markdown and SVG previews, and the modes that show them.</summary>
public class PreviewTests
{
    private static Lazy<HeadlessUnitTestSession> Session => HeadlessWriter.Session;

    /// <summary>An IRIX-era export: no namespace, the 50pt frame, drawn at scale(0.5).</summary>
    private const string LegacySvg = """
        <?xml version="1.0" standalone="no"?>
        <svg width="50pt" height="50pt" viewBox="0 0 50 50" xml:space="preserve">
        <g transform="scale(0.5)">
          <g style="fill:#FFFFFF;stroke:#000000;"><path d="M42.93 64.4L75.00 48.37L75.00 22.5L42.93 38.53z"/></g>
        </g>
        </svg>
        """;

    [Theory]
    [InlineData("/notes/readme.md", "markdown", PreviewKind.Markdown)]
    [InlineData(null, "markdown", PreviewKind.Markdown)]
    [InlineData("/notes/readme.md", null, PreviewKind.None)]
    [InlineData("/icons/arrow.svg", "xml", PreviewKind.Svg)]
    [InlineData("/icons/ARROW.SVG", null, PreviewKind.Svg)]
    [InlineData("/src/main.c", "c", PreviewKind.None)]
    public void MarkdownGoesByLanguageAndSvgByName(string? path, string? language, PreviewKind expected) =>
        Assert.Equal(expected, PreviewKinds.For(path, language));

    [Fact]
    public void ATabOpensInItsKindsDefaultAndTakesTheNewOnesWhenTheKindChanges()
    {
        var defaults = PreviewDefaults.Initial with { Markdown = ViewMode.Preview, Svg = ViewMode.Split };
        var document = new DocumentViewModel(1, TabSettings.Default);
        using var tab = new EditorTabViewModel(document, new EditorPreferences(), defaults.For);

        Assert.Equal(ViewMode.Editor, tab.Mode);

        // Switched to Markdown from the language menu: it now opens as Markdown would.
        document.LanguageId = PreviewKinds.MarkdownLanguage;
        Assert.Equal(ViewMode.Preview, tab.Mode);

        // A change of mode after that is the user's, and stays until the kind changes again.
        tab.Choose(ViewMode.Split);
        Assert.Equal(ViewMode.Split, tab.Mode);
        document.LanguageId = PreviewKinds.MarkdownLanguage;
        Assert.Equal(ViewMode.Split, tab.Mode);
    }

    [Fact]
    public void TheModesEachKindOpensInAreRememberedByName()
    {
        var directory = Directory.CreateTempSubdirectory("wlrix-writer-preview-");
        try
        {
            var path = Path.Combine(directory.FullName, "state.json");
            new WriterState(path).Update(state => state with
            {
                PreviewModes = state.PreviewModes.With(PreviewKind.Svg, ViewMode.Preview),
            });

            Assert.Contains("\"preview\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            var reread = new WriterState(path).Document.PreviewModes;
            Assert.Equal(ViewMode.Preview, reread.Svg);
            Assert.Equal(ViewMode.Split, reread.Markdown);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public Task MarkdownRendersEachBlockAndKnowsWhereItCameFrom() =>
        Session.Value.Dispatch(() =>
        {
            const string markdown = """
                # Title

                A paragraph with **bold**, `code` and a [link](https://example.com).

                - one
                - [x] two

                | a | b |
                |---|--:|
                | 1 | 2 |

                ```
                code block
                ```
                """;
            var opened = new List<string>();
            var rendered = new MarkdownRenderer(Palette(), null, opened.Add).Render(markdown);

            var lines = rendered.Blocks.Select(block => block.Line).ToList();
            // Title, paragraph, the list, its two items, the table, the code.
            Assert.Equal([0, 2, 4, 4, 5, 7, 11], lines);
            Assert.True(rendered.Anchors.ContainsKey("title"));

            var paragraph = Assert.IsType<SelectableTextBlock>(rendered.Blocks[1].Block);
            Assert.Equal("A paragraph with bold, code and a link.", Flatten(paragraph));
        }, CancellationToken.None);

    [Fact]
    public Task AnImageOnTheNetworkShowsItsAltTextRatherThanBeingFetched() =>
        Session.Value.Dispatch(() =>
        {
            var rendered = new MarkdownRenderer(Palette(), null, _ => { })
                .Render("![the logo](https://example.com/logo.png)");

            var paragraph = Assert.IsType<SelectableTextBlock>(rendered.Blocks[0].Block);
            Assert.Equal("[the logo]", Flatten(paragraph));
        }, CancellationToken.None);

    [Fact]
    public Task ASplitMarkdownTabShowsTheTextThePreviewAndTheButtons() =>
        Session.Value.Dispatch(() =>
        {
            using var view = new Harness("# Heading\n\nSome *text*.\n", "/tmp/readme.md", "markdown", ViewMode.Split);

            Assert.True(view.Find<Control>("TextWell").IsVisible);
            Assert.True(view.Find<Control>("PreviewWell").IsVisible);
            Assert.True(view.Find<Control>("ModeButtons").IsVisible);
            Assert.True(view.Find<ToggleButton>("SplitButton").IsChecked);

            // Preview only: the text's column gives its width up entirely.
            view.Tab.Choose(ViewMode.Preview);
            view.Settle();
            Assert.False(view.Find<Control>("TextWell").IsVisible);
            Assert.True(view.Find<ToggleButton>("PreviewOnlyButton").IsChecked);
        }, CancellationToken.None);

    [Fact]
    public Task APlainTabHasNoPreviewWhateverItsModeSays() =>
        Session.Value.Dispatch(() =>
        {
            using var view = new Harness("int main;", "/tmp/main.c", "c", ViewMode.Preview);

            Assert.True(view.Find<Control>("TextWell").IsVisible);
            Assert.False(view.Find<Control>("PreviewWell").IsVisible);
            Assert.False(view.Find<Control>("ModeButtons").IsVisible);
        }, CancellationToken.None);

    [Fact]
    public Task AnIrixEraSvgIsDrawnInThePreview() =>
        Session.Value.Dispatch(() =>
        {
            using var view = new Harness(LegacySvg, "/tmp/arrow.svg", "xml", ViewMode.Preview);

            var image = view.WaitFor(() => view.Find<PreviewPane>("Preview").GetVisualDescendants()
                .OfType<Image>().FirstOrDefault(found => found.Source is not null));
            // Fitted to the pane, keeping its square shape.
            Assert.Equal(image.Width, image.Height, precision: 0);
            Assert.True(image.Width > 100);
        }, CancellationToken.None);

    private static MarkdownPalette Palette() => new(
        Brushes.Black, Brushes.Gray, Brushes.Blue, Brushes.LightGray, Brushes.Gray,
        FontFamily.Default, new FontFamily(HeadlessWriter.Font), 13);

    private static string Flatten(TextBlock block) => string.Concat(block.Inlines!.Select(Text));

    private static string Text(Inline inline) => inline switch
    {
        Run run => run.Text ?? "",
        Span span => string.Concat(span.Inlines.Select(Text)),
        _ => "",
    };

    /// <summary>An editor view showing one tab, in a window of its own.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly Window _window;

        public Harness(string text, string path, string? language, ViewMode mode)
        {
            var document = new DocumentViewModel(path, new LoadedFile(text, TextFormat.Default, default), language,
                TabSettings.Default);
            Tab = new EditorTabViewModel(document, new EditorPreferences(), _ => mode);
            View = new EditorView { DataContext = Tab };
            _window = new Window { Width = 700, Height = 400, Content = View };
            _window.Show();
            Settle();
        }

        public EditorTabViewModel Tab { get; }

        public EditorView View { get; }

        public T Find<T>(string name) where T : Control => View.FindControl<T>(name)!;

        public void Settle()
        {
            for (var i = 0; i < 6; i++)
                Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Runs the dispatcher until <paramref name="probe"/> finds something, or gives up.</summary>
        public T WaitFor<T>(Func<T?> probe) where T : class
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Settle();
                if (probe() is { } found)
                    return found;
                Thread.Sleep(20);
            }
            throw new TimeoutException("the preview never rendered");
        }

        public void Dispose()
        {
            _window.Close();
            Tab.Dispose();
        }
    }
}
