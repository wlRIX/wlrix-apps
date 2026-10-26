using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Dnd;
using Wlrix.Icons;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.Core.State;
using Wlrix.Files.Core.Thumbnails;
using Wlrix.Files.Services;
using Wlrix.Files.ViewModels;
using Wlrix.Files.Views;
using ReactiveUI.Avalonia;
using Xunit;
using Xunit.Abstractions;
using Location = Wlrix.Files.Core.Location;

namespace Wlrix.Files.Tests;

/// <summary>
/// The window with no display: which control has keyboard focus, and so whether the listing's
/// Cut, Copy and Paste keys reach it.
/// </summary>
public sealed class ListingFocusTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wlrix-listing-focus", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public Task CopyInOneTabPastesIntoAnEmptyFolderInAnotherWithTheKeys() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            var (from, to) = TwoFolders();
            using var harness = new Harness(_root, Location.FromLocalPath(from), output);
            harness.WaitFor(() => harness.Model.Pane.Entries.Count == 1, "the first listing");

            // Nothing clicked yet: the listing has focus because it is what the window opened on.
            harness.Window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            harness.Report("after Ctrl+A");
            Assert.Single(harness.Model.Selection);
            harness.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            Assert.True(harness.Clipboard.HasContent, "Ctrl+C put nothing on the clipboard");

            // The new tab's folder is empty, so there is no row to take focus: the view must.
            harness.Model.AddTab(Location.FromLocalPath(to));
            harness.Pump();
            harness.Report("after the new tab");
            harness.Window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
            harness.WaitFor(() => File.Exists(Path.Combine(to, "note.txt")), "the pasted file");
        }, CancellationToken.None);

    [Fact]
    public Task ClickingEmptySpaceTakesTheKeysBackFromThePathBar() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            var (from, to) = TwoFolders();
            using var harness = new Harness(_root, Location.FromLocalPath(to), output);
            harness.Clipboard.Copy([Location.FromLocalPath(Path.Combine(from, "note.txt"))]);

            harness.PathField.Focus();
            harness.Pump();
            var view = harness.Window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "IconView");
            var empty = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height - 30), harness.Window)!.Value;
            harness.Window.MouseDown(empty, MouseButton.Left);
            harness.Window.MouseUp(empty, MouseButton.Left);
            harness.Pump();
            harness.Report("after clicking empty space");

            harness.Window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
            harness.WaitFor(() => File.Exists(Path.Combine(to, "note.txt")), "the pasted file");
        }, CancellationToken.None);

    [Fact]
    public Task ThePathBarKeepsSelectAllForItsText() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            var (from, _) = TwoFolders();
            using var harness = new Harness(_root, Location.FromLocalPath(from), output);
            harness.WaitFor(() => harness.Model.Pane.Entries.Count == 1, "the first listing");

            harness.PathField.Focus();
            harness.Pump();
            harness.Window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            harness.Pump();

            Assert.Empty(harness.Model.Selection);
            Assert.Equal(harness.PathField.Text, harness.PathField.SelectedText);
        }, CancellationToken.None);

    [Fact]
    public Task EveryRowCarriesItsToolTipInBothViews() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            // Bound in the item templates, where a wrong path fails silently: the tip is
            // simply never there. So it is read off the cells the views actually made.
            var (from, _) = TwoFolders();
            using var harness = new Harness(_root, Location.FromLocalPath(from), output);
            harness.WaitFor(() => harness.Model.Pane.Entries.Count == 1, "the first listing");
            var expected = harness.Model.Pane.Entries[0].ToolTipText;

            Assert.Equal(expected, harness.TipShown());
            harness.Model.Pane.ViewMode = ViewMode.Details;
            harness.Pump();
            Assert.Equal(expected, harness.TipShown());
        }, CancellationToken.None);

    private (string From, string To) TwoFolders()
    {
        var from = Directory.CreateDirectory(Path.Combine(_root, "from")).FullName;
        var to = Directory.CreateDirectory(Path.Combine(_root, "to")).FullName;
        File.WriteAllText(Path.Combine(from, "note.txt"), "x");
        return (from, to);
    }

    /// <summary>The file manager's window, built as App builds it but with nothing on the bus.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly ITestOutputHelper _output;

        public Harness(string root, Location start, ITestOutputHelper output)
        {
            _output = output;
            // ReactiveUI notices the test runner and schedules on a thread of its own, which
            // the menus then read their commands from and throw. The app's scheduler, as the
            // application has it.
            ReactiveUI.RxSchedulers.MainThreadScheduler = AvaloniaScheduler.Instance;
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<ICredentialStore, TransientCredentialStore>();
            services.AddSingleton(_ => new FileSystemProvider());
            services.AddSingleton(_ => XdgUserDirs.Read());
            services.AddSingleton(_ => MountTable.Read());
            // The suite's fixture database, never the machine's: see SharedMimeDatabase.Load.
            services.AddSingleton(_ => SharedMimeDatabase.Load([Path.Combine(AppContext.BaseDirectory, "Fixtures", "mime")]));
            services.AddSingleton<FilesSettings>();
            services.AddSingleton(sp => sp.GetRequiredService<FilesSettings>().Current);
            services.AddSingleton(_ => new XdgIconTheme());
            services.AddSingleton(sp => new IconLoader(sp.GetRequiredService<XdgIconTheme>()));
            services.AddSingleton<IconService>();
            services.AddSingleton(_ => new ThumbnailCache(Path.Combine(root, "thumbnails")));
            services.AddSingleton(sp => new ThumbnailProvider(sp.GetRequiredService<ThumbnailCache>()));
            services.AddSingleton<ThumbnailService>();
            services.AddSingleton(_ => new ApplicationHandlers([]));
            services.AddSingleton<ApplicationLauncher>();
            services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<FileSystemProvider>());
            services.AddSingleton(sp => new OperationQueue(
                sp.GetRequiredService<IFileSystemProvider>(), sp.GetRequiredService<MountTable>()));
            services.AddSingleton<FileClipboard>();
            services.AddSingleton<OperationsMonitor>();
            services.AddSingleton(sp => new DragStaging(sp.GetRequiredService<IFileSystemProvider>()));
            services.AddSingleton(_ => new FilesStateStore(Path.Combine(root, "state")));
            services.AddSingleton<IStorageDeviceMonitor, NoDevices>();
            services.AddSingleton<IWindowRouting<MainWindowViewModel>, NoRouting>();
            services.AddTransient<MainWindowViewModel>();
            _services = services.BuildServiceProvider();

            Model = _services.GetRequiredService<MainWindowViewModel>();
            Model.Pane.ResetTo(start);
            Window = new MainWindow { DataContext = Model, Width = 800, Height = 500 };
            Window.Show();
            _ = Model.StartAsync();
            Pump();
        }

        public MainWindowViewModel Model { get; }
        public MainWindow Window { get; }
        public FileClipboard Clipboard => _services.GetRequiredService<FileClipboard>();

        /// <summary>The tooltip on the visible listing's first row, wherever in the cell it sits.</summary>
        public object? TipShown()
        {
            var view = Window.GetVisualDescendants().OfType<ListBox>()
                .Single(box => box.Name is "IconView" or "Listing" && box.IsEffectivelyVisible);
            return view.GetRealizedContainers().First().GetVisualDescendants()
                .OfType<Control>().Select(ToolTip.GetTip).FirstOrDefault(tip => tip is not null);
        }

        public TextBox PathField =>
            Window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PART_TextBox");

        public void Pump()
        {
            for (var i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        public void WaitFor(Func<bool> done, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!done())
            {
                if (DateTime.UtcNow > until)
                {
                    Report($"gave up waiting for {what}");
                    Assert.Fail($"timed out waiting for {what}");
                }
                Pump();
                Thread.Sleep(10);
            }
        }

        public void Report(string when)
        {
            var focused = Window.FocusManager?.GetFocusedElement();
            var chain = (focused as Visual)?.GetSelfAndVisualAncestors()
                .Select(visual => visual.GetType().Name).Take(6) ?? [];
            _output.WriteLine($"{when}: focus on {string.Join(" < ", chain)}; " +
                              $"selection {Model.Selection.Count}");
        }

        public void Dispose()
        {
            Window.Close();
            // The operation queue only disposes asynchronously.
            _ = _services.DisposeAsync();
        }
    }

    private sealed class NoDevices : IStorageDeviceMonitor
    {
        public IReadOnlyList<StorageDevice> Devices => [];
        public event Action? Changed { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<(string? MountPoint, string? Problem)> MountAsync(StorageDevice device) =>
            Task.FromResult<(string?, string?)>((null, null));
        public Task<string?> UnmountAsync(StorageDevice device) => Task.FromResult<string?>(null);
    }

    private sealed class NoRouting : IWindowRouting<MainWindowViewModel>
    {
        public void Open(Location location, OpenIntent intent, MainWindowViewModel from) { }
        public MainWindowViewModel? Claim(MainWindowViewModel window, Location location) => null;
        public void Resync(MainWindowViewModel window, Location location) { }
        public void Present(MainWindowViewModel window) { }
    }
}

/// <summary>The file manager's own application, on the headless platform.</summary>
/// <remarks>
/// One session for the whole assembly, and every test that touches the dispatcher goes
/// through it. Starting a session gives <c>Dispatcher.UIThread</c> to the session's thread for
/// the rest of the run, so a test that ran the dispatcher from its own thread passed or failed
/// by whether it happened to run before the first window was made.
/// </remarks>
public static class HeadlessFiles
{
    private static readonly Lazy<HeadlessUnitTestSession> Started = new(() =>
        HeadlessUnitTestSession.StartNew(typeof(HeadlessFiles)));

    public static HeadlessUnitTestSession Session => Started.Value;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // As Program has it: without it ReactiveUI raises CanExecuteChanged off the UI
            // thread, and a command bound in markup throws on the first change.
            .UseReactiveUI();
}
