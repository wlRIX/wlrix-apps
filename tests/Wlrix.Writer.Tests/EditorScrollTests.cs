// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Wlrix.Writer.Views;
using Xunit;

namespace Wlrix.Writer.Tests;

/// <summary>
/// Dragging a selection into the corners of the text, where the editor once froze.
/// </summary>
/// <remarks>
/// The freeze needed TextMate to keep it going and a compositor to show it, but its cause is
/// visible without either: the drag asked for a scroll offset past the end of the text, and
/// layout clamped it back, round after round. These tests watch every offset the view takes
/// and fail on any past the end, where the freeze would hang them instead.
/// </remarks>
public class EditorScrollTests
{
    private static Lazy<HeadlessUnitTestSession> Session => HeadlessWriter.Session;

    [Fact]
    public Task DraggingPastTheEndOfTheLongestLineStaysInsideTheText() =>
        Session.Value.Dispatch(() =>
        {
            var lines = Enumerable.Range(0, 40).Select(i => $"line {i}").ToArray();
            lines[5] = "long " + new string('x', 400);
            using var editor = new Harness(string.Join('\n', lines));

            var y = editor.LineHeight * 5.5;
            editor.Drag(new Point(2, y), i => new Point(Math.Min(600, 10 + i * 20), y));

            Assert.Equal(406, editor.Editor.TextArea.Caret.Column);
            editor.AssertNeverPastTheEnd();
        }, CancellationToken.None);

    [Fact]
    public Task DraggingPastTheLastLineStaysInsideTheText() =>
        Session.Value.Dispatch(() =>
        {
            using var editor = new Harness(string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i}")));

            editor.Drag(new Point(2, 2), i => new Point(20, Math.Min(400, 10 + i * 10)));

            Assert.Equal(40, editor.Editor.TextArea.Caret.Line);
            editor.AssertNeverPastTheEnd();
        }, CancellationToken.None);

    /// <summary>A Writer editor in a small window, with every scroll offset it takes recorded.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly Window _window;
        private readonly List<(Vector Offset, Size Extent, Size Viewport)> _offsets = [];

        public Harness(string text)
        {
            Editor = new WriterTextEditor { Text = text, FontFamily = new FontFamily(HeadlessWriter.Font), FontSize = 12 };
            Editor.Classes.Add("writer");
            // As EditorView sets it, and the reason the last line has no room to spare.
            Editor.Options.AllowScrollBelowDocument = false;

            _window = new Window { Width = 400, Height = 200, Content = Editor };
            _window.Show();
            Pump();

            var view = Editor.TextArea.TextView;
            view.ScrollOffsetChanged += (_, _) =>
            {
                var scrollable = (IScrollable)view;
                _offsets.Add((view.ScrollOffset, scrollable.Extent, scrollable.Viewport));
            };
        }

        public WriterTextEditor Editor { get; }

        public double LineHeight => Editor.TextArea.TextView.DefaultLineHeight;

        /// <summary>Presses at <paramref name="start"/> and drags, a step at a time, to where each step says.</summary>
        public void Drag(Point start, Func<int, Point> step)
        {
            var view = Editor.TextArea.TextView;
            _window.MouseDown(view.TranslatePoint(start, _window)!.Value, MouseButton.Left);
            for (var i = 0; i < 200; i++)
            {
                _window.MouseMove(view.TranslatePoint(step(i), _window)!.Value, RawInputModifiers.LeftMouseButton);
                Pump();
            }
        }

        public void AssertNeverPastTheEnd()
        {
            Assert.NotEmpty(_offsets);
            foreach (var (offset, extent, viewport) in _offsets)
            {
                Assert.InRange(offset.X, 0, Math.Max(0, extent.Width - viewport.Width) + 0.01);
                Assert.InRange(offset.Y, 0, Math.Max(0, extent.Height - viewport.Height) + 0.01);
            }
        }

        public void Dispose() => _window.Close();

        private static void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}

/// <summary>The Writer's application, styles and all, on a headless platform that draws real text.</summary>
public static class HeadlessWriter
{
    /// <summary>
    /// The one headless application every test class shares. A second session in the same
    /// process collides with the first: Avalonia's URI schemes and dispatcher are process-wide.
    /// </summary>
    public static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.StartNew(typeof(HeadlessWriter)));

    /// <summary>
    /// An installed family, for the reason Program gives: AvaloniaEdit needs a default family
    /// before any style has given it one.
    /// </summary>
    public static readonly string Font = PickFont();

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = Font });

    private static string PickFont()
    {
        string[] wanted = ["DejaVu Sans Mono", "Liberation Mono", "DejaVu Sans", "Noto Sans"];
        var installed = SKFontManager.Default.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.FirstOrDefault(installed.Contains) ?? installed.First();
    }
}
