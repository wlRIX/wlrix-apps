// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wlrix.Console.Services;
using Wlrix.Console.ViewModels;
using Wlrix.Console.Views;
using Xunit;

namespace Wlrix.Console.Tests;

/// <summary>
/// The log view over a full tab of lines: what used to be one TextBox laying out a week of log.
/// </summary>
public sealed class LogViewTests : IDisposable
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.StartNew(typeof(LogViewTests)));

    private readonly string _dir = Directory.CreateTempSubdirectory("wlrix-console-tests-").FullName;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public Task AFullLogBuildsOnlyTheRowsOnScreenAndOpensAtItsEnd() =>
        Session.Value.Dispatch(() =>
        {
            var log = Fill("full.log", LogViewModel.MaxLines);
            var (window, view) = Show(log);

            var rows = view.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.InRange(rows.Count, 1, 100);
            Assert.Contains(rows, row => (string?)row.Content == $"line {LogViewModel.MaxLines - 1}");
            Assert.True(IsAtEnd(view));
            window.Close();
        }, CancellationToken.None);

    [Fact]
    public Task NewLinesAreFollowedOnlyFromTheEnd() =>
        Session.Value.Dispatch(() =>
        {
            var posted = new Queue<Action>();
            var path = Path.Combine(_dir, "live.log");
            File.WriteAllText(path, string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n")));
            var tailer = new LogTailer(path);
            var log = new LogViewModel("Live", tailer, 1000, posted.Enqueue);
            tailer.ReadNew();
            Flush(posted);
            var (window, view) = Show(log);
            Assert.True(IsAtEnd(view));

            File.AppendAllText(path, "followed\n");
            tailer.ReadNew();
            Flush(posted);
            Pump();
            Assert.True(IsAtEnd(view));
            Assert.Equal("followed", log.Lines[^1]);

            // Scrolled up to read something: new lines must not pull the view away from it.
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height / 2);
            Pump();
            File.AppendAllText(path, "not followed\n");
            tailer.ReadNew();
            Flush(posted);
            Pump();
            Assert.False(IsAtEnd(view));
            window.Close();
        }, CancellationToken.None);

    [Fact]
    public Task SelectedLinesCopyInLogOrder() =>
        Session.Value.Dispatch(() =>
        {
            var log = Fill("copy.log", 10);
            var (window, view) = Show(log);
            var list = view.FindControl<ListBox>("List")!;

            list.Selection.Select(7);
            list.Selection.Select(2);
            list.Selection.Select(5);

            Assert.Equal("line 2\nline 5\nline 7", view.SelectedText());
            window.Close();
        }, CancellationToken.None);

    private LogViewModel Fill(string name, int count)
    {
        var posted = new Queue<Action>();
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, count).Select(i => $"line {i}\n")));
        var tailer = new LogTailer(path);
        var log = new LogViewModel(name, tailer, LogViewModel.MaxLines, posted.Enqueue);
        tailer.ReadNew();
        Flush(posted);
        return log;
    }

    private static (Window Window, LogView View) Show(LogViewModel log)
    {
        var view = new LogView { DataContext = log };
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        Pump();
        return (window, view);
    }

    private static bool IsAtEnd(LogView view)
    {
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        return scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
    }

    private static void Flush(Queue<Action> posted)
    {
        while (posted.TryDequeue(out var action))
            action();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }
}
