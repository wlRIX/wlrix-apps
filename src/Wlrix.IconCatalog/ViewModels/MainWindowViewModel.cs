// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Common.Desktop;
using Wlrix.IconCatalog.Localization;
using Wlrix.IconCatalog.Models;
using Wlrix.IconCatalog.Services;

namespace Wlrix.IconCatalog.ViewModels;

/// <summary>The catalog window: its pages, the one showing, and the menu commands.</summary>
public sealed class MainWindowViewModel : ReactiveObject, IDisposable
{
    private readonly CatalogState _state;
    private readonly IconImages _images;
    private readonly RunningApps _running;
    private readonly Launcher _launcher;
    private readonly ApplicationWatcher _watcher;
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly List<IDisposable> _subscriptions = [];

    private IReadOnlyList<DesktopEntry> _installed = [];
    private PageViewModel? _currentPage;
    private string? _hint;
    private int _pageCount;
    private bool _started;
    private bool _scanning;
    private bool _rescanAgain;

    public MainWindowViewModel(
        CatalogState state, IconImages images, RunningApps running, Launcher launcher, ApplicationWatcher watcher)
    {
        _state = state;
        _images = images;
        _running = running;
        _launcher = launcher;
        _watcher = watcher;

        var current = this.WhenAnyValue(model => model.CurrentPage);
        var isCustom = current.Select(page => page is { IsSystem: false });
        var hasSelection = current
            .Select(page => page?.WhenAnyValue(model => model.HasSelection) ?? Observable.Return(false))
            .Switch();
        // Where the page showing is, which decides whether Up and Down can go anywhere. The
        // count is in it because a rescan can add or remove pages on either side.
        var position = this.WhenAnyValue(model => model.CurrentPage, model => model.PageCount,
            (page, count) => (At: page is null ? -1 : Pages.IndexOf(page), Count: count));

        NewPage = ReactiveCommand.CreateFromTask(NewPageAsync);
        RenamePage = ReactiveCommand.CreateFromTask(RenamePageAsync, isCustom);
        RemovePage = ReactiveCommand.CreateFromTask(RemovePageAsync, isCustom);
        PageUp = ReactiveCommand.Create(() => Step(-1), position.Select(p => p.At > 0));
        PageDown = ReactiveCommand.Create(() => Step(1), position.Select(p => p.At >= 0 && p.At < p.Count - 1));
        Exit = ReactiveCommand.Create(() => ExitRequested?.Invoke());
        OpenSelected = ReactiveCommand.Create(OpenSelection, hasSelection);
        RemoveSelected = ReactiveCommand.Create(RemoveSelection, hasSelection);
        SelectAll = ReactiveCommand.Create(SelectEverything, current.Select(page => page is not null));
        ProductInformation = ReactiveCommand.Create(() => AboutRequested?.Invoke());

        _subscriptions.Add(current.Subscribe(page =>
        {
            this.RaisePropertyChanged(nameof(Title));
            this.RaisePropertyChanged(nameof(Status));
            RememberPage(page);
        }));

        _launcher.LaunchFailed += message => ErrorRequested?.Invoke(message);
        _running.Changed += () => Dispatcher.UIThread.Post(RefreshRunning);
        _watcher.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            // Newly installed applications bring icons the theme may not have had a moment ago.
            _images.Clear();
            _ = RescanAsync();
        });
    }

    /// <summary>Asks the user for a line of text: title, prompt, initial value. Null if canceled.</summary>
    public Func<string, string, string, Task<string?>>? Prompt { get; set; }

    /// <summary>Asks the user to confirm: title, question.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    public event Action? ExitRequested;
    public event Action? AboutRequested;
    public event Action<string>? ErrorRequested;

    public ObservableCollection<PageViewModel> Pages { get; } = [];

    public PageViewModel? CurrentPage
    {
        get => _currentPage;
        set => this.RaiseAndSetIfChanged(ref _currentPage, value);
    }

    /// <summary>How many pages there are, as an observable property.</summary>
    public int PageCount
    {
        get => _pageCount;
        private set => this.RaiseAndSetIfChanged(ref _pageCount, value);
    }

    public string Title => CurrentPage is { } page
        ? Strings.Title(page.Name, Pages.IndexOf(page) + 1, Pages.Count)
        : Strings.AppName;

    /// <summary>The status line: what the pointer is over, or how many icons the page has.</summary>
    public string Status => _hint
        ?? (CurrentPage is { } page ? Strings.IconsShown(page.Icons.Count) : Strings.NoPages);

    public ReactiveCommand<Unit, Unit> NewPage { get; }
    public ReactiveCommand<Unit, Unit> RenamePage { get; }
    public ReactiveCommand<Unit, Unit> RemovePage { get; }
    public ReactiveCommand<Unit, Unit> PageUp { get; }
    public ReactiveCommand<Unit, Unit> PageDown { get; }
    public ReactiveCommand<Unit, Unit> Exit { get; }
    public ReactiveCommand<Unit, Unit> OpenSelected { get; }
    public ReactiveCommand<Unit, Unit> RemoveSelected { get; }
    public ReactiveCommand<Unit, Unit> SelectAll { get; }
    public ReactiveCommand<Unit, Unit> ProductInformation { get; }

    /// <summary>Scans what is installed and shows it, then starts noticing changes.</summary>
    public async Task StartAsync()
    {
        // Read before the first scan, which shows the first page and would otherwise be taken
        // for the user's choice.
        var last = _state.Document.LastPage;
        await RescanAsync();
        if (last is not null && Pages.FirstOrDefault(page => page.Id == last) is { } page)
            CurrentPage = page;
        _started = true;
        RememberPage(CurrentPage);
        _watcher.Start();
        _running.Start();
    }

    // Written as the page changes rather than at exit: the session ends applications with a
    // signal, and a window that never gets to close would never get to save.
    private void RememberPage(PageViewModel? page)
    {
        if (_started && page is not null && page.Id != _state.Document.LastPage)
            _state.Change(document => document.LastPage = page.Id);
    }

    /// <summary>What the status line shows while the pointer is over an icon or a tab; null to go back to the count.</summary>
    public void SetHint(string? hint)
    {
        if (hint == _hint)
            return;
        _hint = hint;
        this.RaisePropertyChanged(nameof(Status));
    }

    /// <summary>Opens one icon: a double-click or Enter.</summary>
    public void Open(IconViewModel icon) => _launcher.Open(icon.Icon);

    /// <summary>Puts dropped files on the current page.</summary>
    public void Drop(IReadOnlyList<string> paths)
    {
        if (CurrentPage is not { } page || paths.Count == 0)
            return;

        var installedIds = _installed.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var directories = DesktopEntryScanner.ApplicationDirectories().ToList();
        var items = paths.Select(path => DroppedFiles.ItemFor(path, directories, installedIds)).ToList();

        _state.Change(document =>
        {
            foreach (var item in items)
                document.AddItem(page.Id, page.IsSystem, item, PageBuilder.InstalledOn(page.Id, item, _installed));
        });
        Rebuild();

        // What was dropped is what is selected afterwards, whether it was new or already there.
        var keys = items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        page.Selection.Clear();
        foreach (var icon in page.Icons.Where(icon => keys.Contains(icon.Key)))
            page.Selection.Add(icon);
    }

    /// <summary>Remembers the window's size for next time.</summary>
    public void SaveWindow(double width, double height) => _state.Change(document =>
    {
        document.WindowWidth = width;
        document.WindowHeight = height;
    });

    /// <summary>The scheme's colors changed: every icon is drawn again.</summary>
    public void Redraw()
    {
        foreach (var icon in Pages.SelectMany(page => page.Icons))
            icon.Refresh(_images, _running, force: true);
    }

    private async Task RescanAsync()
    {
        // A burst of changes during a scan is one more scan afterwards, not one each.
        if (_scanning)
        {
            _rescanAgain = true;
            return;
        }

        _scanning = true;
        try
        {
            do
            {
                _rescanAgain = false;
                _installed = await Task.Run(() => new DesktopEntryScanner().Scan());
                Rebuild();
            }
            while (_rescanAgain);
        }
        finally
        {
            _scanning = false;
        }
    }

    // Pages from what is installed and what the user changed. The page models are kept and
    // updated in place, so the tab showing and the icons selected survive a rescan.
    private void Rebuild()
    {
        var pages = PageBuilder.Build(_installed, _state.Document, Strings.Page,
            path => DroppedFiles.Resolve(path, _culture), _culture);
        var current = CurrentPage;
        var currentIndex = current is null ? 0 : Pages.IndexOf(current);

        var existing = Pages.ToDictionary(page => page.Id, StringComparer.Ordinal);
        foreach (var gone in Pages.Where(page => pages.All(p => p.Id != page.Id)).ToList())
            Pages.Remove(gone);

        for (var i = 0; i < pages.Count; i++)
        {
            if (!existing.TryGetValue(pages[i].Id, out var model))
            {
                model = new PageViewModel(pages[i]);
                Pages.Insert(i, model);
            }
            else if (Pages.IndexOf(model) is var at && at != i)
            {
                Pages.Move(at, i);
            }
            model.Update(pages[i], _images, _running);
        }

        // A page that went away -- its last application uninstalled -- leaves the nearest one showing.
        if (current is null || !Pages.Contains(current))
            CurrentPage = Pages.Count == 0 ? null : Pages[Math.Clamp(currentIndex, 0, Pages.Count - 1)];

        PageCount = Pages.Count;
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Status));
    }

    private void RefreshRunning()
    {
        foreach (var icon in Pages.SelectMany(page => page.Icons))
            icon.Refresh(_images, _running);
    }

    private void Step(int by)
    {
        if (CurrentPage is not { } page)
            return;
        var at = Pages.IndexOf(page) + by;
        if (at >= 0 && at < Pages.Count)
            CurrentPage = Pages[at];
    }

    private async Task NewPageAsync()
    {
        if (Prompt is null || await Prompt(Strings.NewPageTitle, Strings.NewPagePrompt, string.Empty) is not { } name)
            return;

        string id = string.Empty;
        _state.Change(document => id = document.AddPage(name).Id);
        Rebuild();
        CurrentPage = Pages.FirstOrDefault(page => page.Id == id);
    }

    private async Task RenamePageAsync()
    {
        if (CurrentPage is not { IsSystem: false } page || Prompt is null)
            return;
        if (await Prompt(Strings.RenamePageTitle, Strings.RenamePagePrompt(page.Name), page.Name) is not { } name)
            return;

        _state.Change(document =>
        {
            if (document.Custom(page.Id) is { } custom)
                custom.Name = name;
        });
        Rebuild();
    }

    private async Task RemovePageAsync()
    {
        if (CurrentPage is not { IsSystem: false } page)
            return;
        if (Confirm is not null && !await Confirm(Strings.RemovePageTitle, Strings.RemovePageConfirm(page.Name)))
            return;

        _state.Change(document => document.CustomPages.RemoveAll(custom => custom.Id == page.Id));
        Rebuild();
    }

    private void OpenSelection()
    {
        if (CurrentPage is not { } page)
            return;
        foreach (var icon in page.Selection.ToList())
            Open(icon);
    }

    private void RemoveSelection()
    {
        if (CurrentPage is not { } page || page.Selection.Count == 0)
            return;

        var keys = page.Selection.Select(icon => icon.Key).ToList();
        _state.Change(document =>
        {
            foreach (var key in keys)
                document.RemoveItem(page.Id, page.IsSystem, key);
        });
        Rebuild();
    }

    private void SelectEverything()
    {
        if (CurrentPage is not { } page)
            return;
        foreach (var icon in page.Icons.Where(icon => !page.Selection.Contains(icon)))
            page.Selection.Add(icon);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _watcher.Dispose();
        _running.Dispose();
    }
}
