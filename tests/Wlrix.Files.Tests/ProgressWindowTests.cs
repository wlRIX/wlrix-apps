using Avalonia.Headless;
using Avalonia.Threading;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Testing;
using Wlrix.Files.Services;
using Wlrix.Files.ViewModels;
using Xunit;
using Location = Wlrix.Files.Core.Location;

namespace Wlrix.Files.Tests;

/// <summary>The progress window, following a slow copy through the real queue.</summary>
public sealed class ProgressWindowTests
{
    private sealed class SingleFileSystemProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    /// <summary>A copy that takes seconds: handed over 128 KiB at a time, 80 ms apart.</summary>
    /// <param name="mebibytes">4 is about two and a half seconds.</param>
    private static (OperationQueue Queue, FakeFileSystem Fs) SlowCopySetup(int mebibytes = 4)
    {
        var fs = new FakeFileSystem { ReadDelay = TimeSpan.FromMilliseconds(80) }
            .AddFile("/share/linux.iso", new byte[mebibytes * 1024 * 1024])
            .AddDirectory("/home/Downloads");
        return (new OperationQueue(new SingleFileSystemProvider(fs)), fs);
    }

    private static void Pump(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        } while (DateTime.UtcNow < until);
    }

    [Fact]
    public Task TheWindowComesUpForASlowCopyAndGoesWhenItIsDone() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            ReactiveUI.RxSchedulers.MainThreadScheduler = ReactiveUI.Avalonia.AvaloniaScheduler.Instance;
            var (queue, fs) = SlowCopySetup();
            using var monitor = new OperationsMonitor(queue);

            var copy = queue.Enqueue(FileOperation.Copy(
                [Location.Parse("/share/linux.iso")], Location.Parse("/home/Downloads")));
            Pump(TimeSpan.FromMilliseconds(300));
            // Not yet: a paste that is over in a moment never flashes a window.
            Assert.False(monitor.IsShowing);

            Pump(TimeSpan.FromMilliseconds(1500));
            Assert.True(monitor.IsShowing);
            var panel = Assert.Single(monitor.Operations);
            Assert.Equal("linux.iso", panel.Queued.Progress.Current);
            Assert.Contains("linux.iso", panel.Summary);
            Assert.Contains("Downloads", panel.Summary);
            Assert.InRange(panel.Fraction ?? -1, 0.0001, 0.9999);
            Assert.NotEmpty(panel.SpeedHistory);
            Assert.Equal("/share/linux.iso", panel.SourceText);
            Assert.Equal("/home/Downloads", panel.DestinationText);

            // Paused, the copy holds still and says so.
            panel.TogglePause.Execute().Subscribe();
            Pump(TimeSpan.FromMilliseconds(400));
            var held = copy.Progress.BytesDone;
            Pump(TimeSpan.FromMilliseconds(1200));
            Assert.Equal(held, copy.Progress.BytesDone);
            Assert.True(panel.IsPaused);
            Assert.Equal(Localization.Strings.ProgressPaused, panel.Status);
            Assert.Equal(0, panel.SpeedHistory[^1]);

            // Resumed, it finishes, and the window goes with it.
            panel.TogglePause.Execute().Subscribe();
            while (!copy.Completion.IsCompleted)
                Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(200));

            Assert.True(copy.Completion.Result.Succeeded);
            Assert.Equal(4 * 1024 * 1024, fs.ContentOf("/home/Downloads/linux.iso")!.Length);
            Assert.Empty(monitor.Operations);
            Assert.False(monitor.IsShowing);
        }, CancellationToken.None);

    [Fact]
    public Task CancelInThePanelStopsTheCopy() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            ReactiveUI.RxSchedulers.MainThreadScheduler = ReactiveUI.Avalonia.AvaloniaScheduler.Instance;
            var (queue, fs) = SlowCopySetup();
            using var monitor = new OperationsMonitor(queue);

            var copy = queue.Enqueue(FileOperation.Copy(
                [Location.Parse("/share/linux.iso")], Location.Parse("/home/Downloads")));
            Pump(TimeSpan.FromMilliseconds(500));
            Assert.Single(monitor.Operations).Cancel.Execute().Subscribe();
            while (!copy.Completion.IsCompleted)
                Pump(TimeSpan.FromMilliseconds(50));

            Assert.Equal(OperationPhase.Canceled, copy.Completion.Result.Phase);
            Assert.False(fs.Contains("/home/Downloads/linux.iso"));
        }, CancellationToken.None);

    [Fact]
    public Task ClosingTheWindowMidCopyKeepsItAwayUntilAskedFor() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            ReactiveUI.RxSchedulers.MainThreadScheduler = ReactiveUI.Avalonia.AvaloniaScheduler.Instance;
            var (queue, _) = SlowCopySetup(mebibytes: 16);
            using var monitor = new OperationsMonitor(queue);

            var copy = queue.Enqueue(FileOperation.Copy(
                [Location.Parse("/share/linux.iso")], Location.Parse("/home/Downloads")));
            Pump(TimeSpan.FromMilliseconds(1300));
            Assert.True(monitor.IsShowing);

            monitor.CurrentWindow!.Close();
            Pump(TimeSpan.FromMilliseconds(1200));
            Assert.False(monitor.IsShowing);

            // The status bar's click is what brings it back.
            monitor.Show();
            Assert.True(monitor.IsShowing);

            copy.Cancel();
            while (!copy.Completion.IsCompleted)
                Pump(TimeSpan.FromMilliseconds(50));
        }, CancellationToken.None);

    [Theory]
    [InlineData(0.2, "1 s")]
    [InlineData(59.0, "59 s")]
    [InlineData(61.0, "2 min")]
    [InlineData(3700.0, "1 h 2 min")]
    public Task TimeLeftIsInTheCoarsestUsefulUnit(double seconds, string expected) =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            using var _ = English();
            Assert.Equal(expected, OperationViewModel.Remaining(seconds));
        }, CancellationToken.None);

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(900_000_000, 1024.0 * 1024 * 1024)]
    [InlineData(100 * 1024 * 1024, 128.0 * 1024 * 1024)]
    [InlineData(3 * 1024 * 1024, 4.0 * 1024 * 1024)]
    public void TheGraphScaleIsARoundNumber(double fastest, double expected) =>
        Assert.Equal(expected, Controls.SpeedGraph.Scale(fastest));

    private static UiCulture English() => new(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

/// <summary>Sets the UI culture for a test and puts it back after.</summary>
internal sealed class UiCulture : IDisposable
{
    private readonly System.Globalization.CultureInfo _was = System.Globalization.CultureInfo.CurrentUICulture;

    public UiCulture(System.Globalization.CultureInfo culture) =>
        System.Globalization.CultureInfo.CurrentUICulture = culture;

    public void Dispose() => System.Globalization.CultureInfo.CurrentUICulture = _was;
}
