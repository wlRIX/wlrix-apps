// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Models;
using Wlrix.Writer.Services;
using ZLogger;

namespace Wlrix.Writer.ViewModels;

/// <summary>What to do with unsaved changes when their document is closing.</summary>
public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>What the Tabs dialog answered.</summary>
public sealed record TabsDialogResult(TabSettings Settings, bool MakeDefault);

/// <summary>The editor window: its documents, its one or two tab groups, and every menu command.</summary>
public sealed class MainWindowViewModel : ViewModelBase, ISearchTarget, IDisposable
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly WriterState _state;
    private readonly RecentFiles _recent;
    private readonly FileWatcher _watcher = new();
    private readonly List<DocumentViewModel> _documents = [];
    private readonly List<IDisposable> _subscriptions = [];

    private TabGroupViewModel? _secondaryGroup;
    private TabGroupViewModel _activeGroup;
    private EditorTabViewModel? _activeTab;
    private DocumentViewModel? _activeDocument;
    private string _title = "";
    private bool _clipboardHasText;
    private bool _isWindowActive;
    private bool _noticing;

    /// <summary>The design-time constructor. Only the XAML previewer calls this.</summary>
    public MainWindowViewModel()
        : this(NullLogger<MainWindowViewModel>.Instance)
    {
    }

    public MainWindowViewModel(ILogger<MainWindowViewModel> logger)
    {
        _logger = logger;
        _state = new WriterState(warn: message => _logger.ZLogWarning($"{message}"));
        _recent = new RecentFiles(_state.Document.Recent);
        Preferences.ShowLineNumbers = _state.Document.ShowLineNumbers;
        // One history for both dialogs: a string searched for in Find is there to recall in
        // Replace/Find, and the other way round.
        var searchHistory = new SearchHistory(_state.Document.SearchHistory);
        Action<IReadOnlyList<string>> saveHistory =
            history => _state.Update(state => state with { SearchHistory = [.. history] });
        FindOptions = new FindViewModel(this, searchHistory, saveHistory);
        ReplaceOptions = new ReplaceViewModel(this, searchHistory, saveHistory);

        PrimaryGroup = new TabGroupViewModel { IsActive = true };
        _activeGroup = PrimaryGroup;
        _subscriptions.Add(PrimaryGroup.WhenAnyValue(group => group.SelectedTab).Subscribe(_ => OnSelectionChanged(PrimaryGroup)));

        _watcher.Changed += path => Dispatcher.UIThread.Post(() => OnFileChanged(path));

        var hasTab = this.WhenAnyValue(model => model.ActiveTab).Select(tab => tab is not null);
        var activeDocument = this.WhenAnyValue(model => model.ActiveDocument);
        var activeTab = this.WhenAnyValue(model => model.ActiveTab);

        New = ReactiveCommand.Create(NewDocument);
        Open = ReactiveCommand.CreateFromTask(OpenAsync);
        OpenRecent = ReactiveCommand.CreateFromTask<string>(path => OpenPathAsync(path));
        Close = ReactiveCommand.CreateFromTask(() => ActiveTab is { } tab ? CloseTabAsync(tab) : Task.CompletedTask, hasTab);
        Save = ReactiveCommand.CreateFromTask(() => ActiveDocument is { } document ? SaveAsync(document) : Task.CompletedTask, hasTab);
        SaveAs = ReactiveCommand.CreateFromTask(() => ActiveDocument is { } document ? SaveAsAsync(document) : Task.CompletedTask, hasTab);
        Exit = ReactiveCommand.Create(() => ExitRequested?.Invoke());

        Undo = ReactiveCommand.Create(() => Edit(editor => editor.Undo(), document => document.Text.UndoStack.Undo()),
            activeDocument.Select(document => document?.WhenAnyValue(model => model.CanUndo) ?? Observable.Return(false)).Switch());
        Redo = ReactiveCommand.Create(() => Edit(editor => editor.Redo(), document => document.Text.UndoStack.Redo()),
            activeDocument.Select(document => document?.WhenAnyValue(model => model.CanRedo) ?? Observable.Return(false)).Switch());
        var hasSelection = activeTab
            .Select(tab => tab?.WhenAnyValue(model => model.HasSelection) ?? Observable.Return(false)).Switch();
        Cut = ReactiveCommand.Create(() => Edit(editor => editor.Cut()), hasSelection);
        Copy = ReactiveCommand.Create(() => Edit(editor => editor.Copy()), hasSelection);
        Paste = ReactiveCommand.Create(() => Edit(editor => editor.Paste()),
            this.WhenAnyValue(model => model.ActiveTab, model => model.ClipboardHasText, (tab, text) => tab is not null && text));
        SelectAll = ReactiveCommand.Create(() => Edit(editor => editor.SelectAll()), hasTab);

        Find = ReactiveCommand.Create(() => FindDialogRequested?.Invoke(), hasTab);
        Replace = ReactiveCommand.Create(() => ReplaceDialogRequested?.Invoke(), hasTab);
        _subscriptions.Add(hasSelection.Subscribe(selected => ReplaceOptions.HasSelection = selected));

        ChooseLanguage = ReactiveCommand.Create<string?>(SetLanguage, hasTab);
        ToggleHighlight = ReactiveCommand.Create(() =>
        {
            if (ActiveDocument is { } document)
                document.HighlightSyntax = !document.HighlightSyntax;
        }, hasTab);
        EditTabs = ReactiveCommand.CreateFromTask(EditTabsAsync, hasTab);
        ToggleLineNumbers = ReactiveCommand.Create(() =>
        {
            Preferences.ShowLineNumbers = !Preferences.ShowLineNumbers;
            _state.Update(state => state with { ShowLineNumbers = Preferences.ShowLineNumbers });
        });

        var isSplit = this.WhenAnyValue(model => model.SecondaryGroup).Select(group => group is not null);
        SplitWindow = ReactiveCommand.Create(Split, hasTab);
        MoveToOtherSide = ReactiveCommand.Create(MoveActiveTab, hasTab);
        Unsplit = ReactiveCommand.Create(Join, isSplit);
        NextTab = ReactiveCommand.Create(() => CycleTab(1), hasTab);
        PreviousTab = ReactiveCommand.Create(() => CycleTab(-1), hasTab);
        ShowAbout = ReactiveCommand.Create(() => AboutRequested?.Invoke());

        _subscriptions.Add(activeDocument
            .Select(document => document is null
                ? Observable.Return(Strings.AppName)
                : document.WhenAnyValue(model => model.DisplayName, model => model.IsModified,
                    (name, modified) => modified ? Strings.TitleModified(name) : name))
            .Switch()
            .Subscribe(title => Title = title));

        _highlightSyntax = activeDocument
            .Select(document => document?.WhenAnyValue(model => model.HighlightSyntax) ?? Observable.Return(false))
            .Switch()
            .ToProperty(this, model => model.HighlightSyntax);
        _subscriptions.Add(_highlightSyntax);

        foreach (var command in new IHandleObservableErrors[]
                 {
                     New, Open, OpenRecent, Close, Save, SaveAs, Exit, Undo, Redo, Cut, Copy, Paste, SelectAll,
                     ChooseLanguage, ToggleHighlight, EditTabs, ToggleLineNumbers, SplitWindow, MoveToOtherSide,
                     Unsplit, NextTab, PreviousTab, ShowAbout,
                 })
        {
            _subscriptions.Add(command.ThrownExceptions.Subscribe(OnCommandFailed));
        }

        // A window always has a document in it: NEdit opens on an empty Untitled one.
        NewDocument();
    }

    // --- view-facing events --------------------------------------------------------------------

    /// <summary>Asks which files to open. Empty if the user canceled.</summary>
    public event Func<Task<IReadOnlyList<string>>>? OpenFilesRequested;

    /// <summary>Asks where to save, given a suggested name and folder. Null if the user canceled.</summary>
    public event Func<string, string?, Task<string?>>? SaveFileRequested;

    /// <summary>Asks whether to save a document's changes before it closes.</summary>
    public event Func<string, Task<SaveChoice>>? ConfirmSaveRequested;

    /// <summary>Asks whether to reload a file changed on disk. The flag says the buffer has unsaved changes.</summary>
    public event Func<string, bool, Task<bool>>? ReloadRequested;

    /// <summary>Tells the user a file was deleted from under its document.</summary>
    public event Func<string, Task>? DeletedNoticeRequested;

    /// <summary>Opens the Tabs dialog on the given settings.</summary>
    public event Func<TabSettings, Task<TabsDialogResult?>>? TabsDialogRequested;

    /// <summary>Opens the Find dialog, or brings it forward if it is open.</summary>
    public event Action? FindDialogRequested;

    /// <summary>Opens the Replace/Find dialog, or brings it forward if it is open.</summary>
    public event Action? ReplaceDialogRequested;

    public event Action? AboutRequested;
    public event Action? ExitRequested;
    public event Action<string>? ErrorRaised;
    public event Action<string>? InformationRaised;

    /// <summary>Asks for the window to come to the front.</summary>
    public event Action? ActivateRequested;

    // --- state ---------------------------------------------------------------------------------

    public EditorPreferences Preferences { get; } = new();

    /// <summary>The Find dialog's text and options, kept while the dialog is closed.</summary>
    public FindViewModel FindOptions { get; }

    /// <summary>The Replace/Find dialog's text and options, kept while the dialog is closed.</summary>
    public ReplaceViewModel ReplaceOptions { get; }

    /// <summary>The left half, or the whole window when it is not split.</summary>
    public TabGroupViewModel PrimaryGroup { get; }

    /// <summary>The right half of a split window, or null.</summary>
    public TabGroupViewModel? SecondaryGroup
    {
        get => _secondaryGroup;
        private set => this.RaiseAndSetIfChanged(ref _secondaryGroup, value);
    }

    /// <summary>The half the menus act on.</summary>
    public TabGroupViewModel ActiveGroup
    {
        get => _activeGroup;
        set
        {
            if (value != PrimaryGroup && value != SecondaryGroup)
                return;
            _activeGroup.IsActive = false;
            value.IsActive = true;
            this.RaiseAndSetIfChanged(ref _activeGroup, value);
            UpdateActiveTab();
        }
    }

    public EditorTabViewModel? ActiveTab
    {
        get => _activeTab;
        private set => this.RaiseAndSetIfChanged(ref _activeTab, value);
    }

    public DocumentViewModel? ActiveDocument
    {
        get => _activeDocument;
        private set => this.RaiseAndSetIfChanged(ref _activeDocument, value);
    }

    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _highlightSyntax;

    /// <summary>The active document's Highlight Syntax, for the check mark.</summary>
    public bool HighlightSyntax => _highlightSyntax.Value;

    /// <summary>Whether there is text to paste. The view refreshes it as the Edit menu opens.</summary>
    public bool ClipboardHasText
    {
        get => _clipboardHasText;
        set => this.RaiseAndSetIfChanged(ref _clipboardHasText, value);
    }

    /// <summary>Whether the window has the focus. Changes on disk are asked about only while it does.</summary>
    public bool IsWindowActive
    {
        get => _isWindowActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _isWindowActive, value);
            if (value)
                _ = AskAboutDiskChangesAsync();
        }
    }

    /// <summary>File ▸ Open Previous. Refilled as the File menu opens.</summary>
    public ObservableCollection<RecentFileItem> RecentItems { get; } = [];

    /// <summary>Whether Open Previous has anything in it.</summary>
    public bool HasRecentItems => RecentItems.Count > 0;

    /// <summary>Preferences ▸ Language Mode. Filled the first time the Preferences menu opens.</summary>
    public ObservableCollection<LanguageChoice> LanguageItems { get; } = [];

    // --- commands ------------------------------------------------------------------------------

    public ReactiveCommand<Unit, Unit> New { get; }
    public ReactiveCommand<Unit, Unit> Open { get; }
    public ReactiveCommand<string, Unit> OpenRecent { get; }
    public ReactiveCommand<Unit, Unit> Close { get; }
    public ReactiveCommand<Unit, Unit> Save { get; }
    public ReactiveCommand<Unit, Unit> SaveAs { get; }
    public ReactiveCommand<Unit, Unit> Exit { get; }
    public ReactiveCommand<Unit, Unit> Undo { get; }
    public ReactiveCommand<Unit, Unit> Redo { get; }
    public ReactiveCommand<Unit, Unit> Cut { get; }
    public ReactiveCommand<Unit, Unit> Copy { get; }
    public ReactiveCommand<Unit, Unit> Paste { get; }
    public ReactiveCommand<Unit, Unit> SelectAll { get; }
    public ReactiveCommand<Unit, Unit> Find { get; }
    public ReactiveCommand<Unit, Unit> Replace { get; }
    public ReactiveCommand<string?, Unit> ChooseLanguage { get; }
    public ReactiveCommand<Unit, Unit> ToggleHighlight { get; }
    public ReactiveCommand<Unit, Unit> EditTabs { get; }
    public ReactiveCommand<Unit, Unit> ToggleLineNumbers { get; }
    public ReactiveCommand<Unit, Unit> SplitWindow { get; }
    public ReactiveCommand<Unit, Unit> MoveToOtherSide { get; }
    public ReactiveCommand<Unit, Unit> Unsplit { get; }
    public ReactiveCommand<Unit, Unit> NextTab { get; }
    public ReactiveCommand<Unit, Unit> PreviousTab { get; }
    public ReactiveCommand<Unit, Unit> ShowAbout { get; }

    // --- menus ---------------------------------------------------------------------------------

    /// <summary>Refills Open Previous with the recent files that still exist.</summary>
    public void RefreshRecent()
    {
        RecentItems.Clear();
        foreach (var path in _recent.Existing())
            RecentItems.Add(new RecentFileItem(path, OpenRecent));
        this.RaisePropertyChanged(nameof(HasRecentItems));
    }

    /// <summary>Fills Language Mode if it is empty, and checks the active document's language.</summary>
    public void RefreshLanguages()
    {
        if (LanguageItems.Count == 0)
        {
            LanguageItems.Add(new LanguageChoice(null, Strings.Plain, ChooseLanguage));
            foreach (var mode in WriterTheme.Shared.Languages.All)
                LanguageItems.Add(new LanguageChoice(mode.Id, mode.Name, ChooseLanguage));
        }

        var current = ActiveDocument?.LanguageId;
        foreach (var item in LanguageItems)
            item.IsChecked = item.Id == current;
    }

    // --- opening -------------------------------------------------------------------------------

    /// <summary>Opens what another launch of the program was asked to open.</summary>
    public async Task OpenUrisAsync(IReadOnlyList<string> uris)
    {
        ActivateRequested?.Invoke();
        foreach (var uri in uris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
                await OpenPathAsync(parsed.LocalPath, createIfMissing: true);
            else
                _logger.ZLogWarning($"ignored a request to open {uri}, which is not a local file");
        }
    }

    /// <summary>Opens <paramref name="path"/> in a tab, or brings its tab forward if it is already open.</summary>
    /// <param name="path">The file.</param>
    /// <param name="createIfMissing">
    /// Whether a file that does not exist starts a new document by that name rather than being an
    /// error. True for names given on the command line, as every Unix editor treats them.
    /// </param>
    public async Task OpenPathAsync(string path, bool createIfMissing = false)
    {
        var full = Path.GetFullPath(path);
        if (_documents.FirstOrDefault(document => document.Path == full) is { } open)
        {
            ShowDocument(open);
            return;
        }

        if (createIfMissing && !File.Exists(full) && !Directory.Exists(full)
            && Directory.Exists(Path.GetDirectoryName(full)))
        {
            var language = await Task.Run(() => WriterTheme.Shared.Languages.ForPath(full));
            Place(new DocumentViewModel(full, language, _state.Document.DefaultTabs));
            _logger.ZLogInformation($"started new file {full}");
            return;
        }

        LoadedFile file;
        try
        {
            file = await Task.Run(() => DocumentIO.Load(full));
        }
        catch (BinaryFileException)
        {
            ErrorRaised?.Invoke(Strings.NotText(Path.GetFileName(full)));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.ZLogWarning(ex, $"could not open {full}");
            ErrorRaised?.Invoke(Strings.OpenFailed(full, ex.Message));
            return;
        }

        // Somebody may have opened it in the meantime, by a second click on it in a file manager.
        if (_documents.FirstOrDefault(document => document.Path == full) is { } raced)
        {
            ShowDocument(raced);
            return;
        }

        // Off the UI thread: the first call waits for the grammar package to finish loading.
        var detected = await Task.Run(() => WriterTheme.Shared.Languages.ForPath(full));
        _logger.ZLogInformation($"opened {full} ({file.Format.Encoding.WebName}, {file.Format.LineEnding}, {detected ?? "plain"})");
        Place(new DocumentViewModel(full, file, detected, _state.Document.DefaultTabs));
        Remember(full);
    }

    /// <summary>Puts a newly opened document in a tab of the active half.</summary>
    private void Place(DocumentViewModel document)
    {
        // An empty Untitled document that nobody has typed in is replaced rather than kept.
        var replace = ActiveTab is { Document: { IsPristine: true, Views: 1 } } pristine ? pristine : null;

        AddDocument(document);
        var tab = AddTab(ActiveGroup, document);
        if (replace is not null)
            RemoveTab(replace);
        tab.RequestFocus();
    }

    private async Task OpenAsync()
    {
        if (OpenFilesRequested is null)
            return;
        foreach (var path in await OpenFilesRequested())
            await OpenPathAsync(path);
    }

    private void NewDocument()
    {
        var number = UntitledNames.Next(_documents.Where(document => document.Path is null)
            .Select(document => document.UntitledNumber));
        var document = new DocumentViewModel(number, _state.Document.DefaultTabs);
        AddDocument(document);
        AddTab(ActiveGroup, document).RequestFocus();
    }

    private void Remember(string path)
    {
        _recent.Add(path);
        _state.Update(state => state with { Recent = [.. _recent.All] });
    }

    // --- saving --------------------------------------------------------------------------------

    /// <summary>Saves <paramref name="document"/>, asking for a name if it has none.</summary>
    /// <returns>Whether it was saved; false if the user canceled or the write failed.</returns>
    public Task<bool> SaveAsync(DocumentViewModel document) =>
        document.Path is { } path ? WriteAsync(document, path) : SaveAsAsync(document);

    private async Task<bool> SaveAsAsync(DocumentViewModel document)
    {
        if (SaveFileRequested is null)
            return false;

        var folder = document.Path is { } current ? Path.GetDirectoryName(current) : null;
        var path = await SaveFileRequested(document.DisplayName, folder);
        if (path is null)
            return false;

        var full = Path.GetFullPath(path);

        // Saving as a file that is open in another tab would leave two documents claiming one
        // file. The other one is closed: the user just chose to overwrite that file, and the
        // save dialog has already asked whether they meant to.
        if (_documents.FirstOrDefault(other => other != document && other.Path == full) is { } other)
            CloseDocumentWithoutAsking(other);

        return await WriteAsync(document, full);
    }

    private async Task<bool> WriteAsync(DocumentViewModel document, string path)
    {
        var text = document.Text.Text;
        var format = document.Format;
        DiskStamp stamp;
        TextFormat written;
        try
        {
            (stamp, written) = await Task.Run(() => DocumentIO.Save(path, text, format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.ZLogWarning(ex, $"could not save {path}");
            ErrorRaised?.Invoke(Strings.SaveFailed(path, ex.Message));
            return false;
        }

        var renamed = document.Path != path;
        if (renamed && document.Path is { } old)
            Unwatch(document, old);

        document.MarkSaved(path, stamp, written);
        document.PendingNotice = DiskNotice.None;

        if (renamed)
        {
            Watch(document, path);
            if (!document.LanguageChosen)
                document.LanguageId = WriterTheme.Shared.Languages.ForPath(path);
        }

        _logger.ZLogInformation($"saved {path}");
        Remember(path);

        if (written != format)
            InformationRaised?.Invoke(Strings.EncodingWidened(document.DisplayName, format.Encoding.WebName));
        return true;
    }

    // --- closing -------------------------------------------------------------------------------

    /// <summary>Closes a tab, asking about unsaved changes if it is the document's last one.</summary>
    /// <returns>Whether it closed.</returns>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        var document = tab.Document;
        if (document.Views == 1 && document.IsModified && !await ResolveChangesAsync(document))
            return false;

        RemoveTab(tab);
        return true;
    }

    /// <summary>
    /// Asks about every document with unsaved changes, for closing the window.
    /// </summary>
    /// <returns>True if the window can close: every one was saved or let go.</returns>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var document in _documents.Where(document => document.IsModified).ToList())
        {
            ShowDocument(document);
            if (!await ResolveChangesAsync(document))
                return false;
        }

        return true;
    }

    /// <summary>Whether any document has unsaved changes.</summary>
    public bool HasUnsavedChanges => _documents.Any(document => document.IsModified);

    private async Task<bool> ResolveChangesAsync(DocumentViewModel document)
    {
        var choice = ConfirmSaveRequested is null ? SaveChoice.Cancel : await ConfirmSaveRequested(document.DisplayName);
        return choice switch
        {
            SaveChoice.Save => await SaveAsync(document),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    private void CloseDocumentWithoutAsking(DocumentViewModel document)
    {
        foreach (var group in Groups().ToList())
        {
            if (group.TabFor(document) is { } tab)
                RemoveTab(tab);
        }
    }

    // --- tabs and groups -----------------------------------------------------------------------

    private IEnumerable<TabGroupViewModel> Groups()
    {
        yield return PrimaryGroup;
        if (SecondaryGroup is { } secondary)
            yield return secondary;
    }

    private TabGroupViewModel GroupOf(EditorTabViewModel tab) =>
        Groups().First(group => group.Tabs.Contains(tab));

    private void AddDocument(DocumentViewModel document)
    {
        _documents.Add(document);
        ReplaceOptions.HasOtherDocuments = _documents.Count > 1;
        if (document.Path is { } path)
            Watch(document, path);
    }

    private EditorTabViewModel AddTab(TabGroupViewModel group, DocumentViewModel document,
        Action<EditorTabViewModel>? prepare = null)
    {
        var tab = new EditorTabViewModel(document, Preferences);
        prepare?.Invoke(tab);
        var index = group.SelectedTab is { } selected ? group.Tabs.IndexOf(selected) + 1 : group.Tabs.Count;
        group.Tabs.Insert(index, tab);
        group.SelectedTab = tab;
        ActiveGroup = group;
        return tab;
    }

    /// <summary>Takes a tab out of its group; a document with no tabs left is closed with it.</summary>
    private void RemoveTab(EditorTabViewModel tab)
    {
        var group = GroupOf(tab);
        var index = group.Tabs.IndexOf(tab);
        group.Tabs.Remove(tab);
        if (group.SelectedTab == tab || group.SelectedTab is null)
            group.SelectedTab = group.Tabs.Count == 0 ? null : group.Tabs[Math.Min(index, group.Tabs.Count - 1)];

        var document = tab.Document;
        tab.Dispose();
        if (document.Views == 0)
        {
            _documents.Remove(document);
            ReplaceOptions.HasOtherDocuments = _documents.Count > 1;
            if (document.Path is { } path)
                Unwatch(document, path);
            document.Dispose();
        }

        CollapseIfEmpty(group);
        UpdateActiveTab();
        ActiveTab?.RequestFocus();
    }

    /// <summary>Closes a half that has run out of tabs, and keeps the window from ever being empty.</summary>
    private void CollapseIfEmpty(TabGroupViewModel group)
    {
        if (group.Tabs.Count > 0)
            return;

        if (SecondaryGroup is { } secondary)
        {
            // The right half's tabs move left when the left half empties, so the remaining
            // half is always the primary one.
            if (group == PrimaryGroup)
            {
                foreach (var tab in secondary.Tabs.ToList())
                {
                    secondary.Tabs.Remove(tab);
                    PrimaryGroup.Tabs.Add(tab);
                }

                PrimaryGroup.SelectedTab = secondary.SelectedTab ?? PrimaryGroup.Tabs.FirstOrDefault();
            }

            SecondaryGroup = null;
            ActiveGroup = PrimaryGroup;
            return;
        }

        // The last tab of the window. NEdit closing its last document leaves an Untitled one in
        // its place, and so does this.
        NewDocument();
    }

    /// <summary>Windows ▸ Split Window: shows the active document in the other half as well.</summary>
    private void Split()
    {
        if (ActiveTab is not { } tab)
            return;

        var other = OtherGroup(GroupOf(tab));
        if (other.TabFor(tab.Document) is { } existing)
        {
            other.SelectedTab = existing;
            ActiveGroup = other;
        }
        else
        {
            // The new view starts where the old one is, as NEdit's split pane did, rather than
            // at the top of the file with the caret before the first character.
            // Set before the tab is added, which may put it straight into an editor.
            var source = tab.Editor;
            AddTab(other, tab.Document, copy =>
            {
                copy.CaretOffset = source?.CaretOffset ?? tab.CaretOffset;
                copy.ScrollOffset = source is null
                    ? tab.ScrollOffset
                    : new global::Avalonia.Vector(source.HorizontalOffset, source.VerticalOffset);
            });
        }

        ActiveTab?.RequestFocus();
    }

    /// <summary>Windows ▸ Move Tab to Other Side.</summary>
    private void MoveActiveTab()
    {
        if (ActiveTab is not { } tab)
            return;

        var source = GroupOf(tab);
        var target = OtherGroup(source);
        if (target.TabFor(tab.Document) is { } existing)
        {
            // Already over there: moving it is just closing this view of it.
            RemoveTab(tab);
            target.SelectedTab = existing;
            ActiveGroup = target;
        }
        else
        {
            var index = source.Tabs.IndexOf(tab);
            source.Tabs.Remove(tab);
            if (source.SelectedTab is null && source.Tabs.Count > 0)
                source.SelectedTab = source.Tabs[Math.Min(index, source.Tabs.Count - 1)];
            target.Tabs.Add(tab);
            target.SelectedTab = tab;
            ActiveGroup = target;

            // Emptying the left half promotes the right one to be the whole window.
            CollapseIfEmpty(source);
            UpdateActiveTab();
        }

        ActiveTab?.RequestFocus();
    }

    /// <summary>Windows ▸ Unsplit Window: everything back into one half.</summary>
    private void Join()
    {
        if (SecondaryGroup is not { } secondary)
            return;

        var selected = ActiveGroup.SelectedTab;
        foreach (var tab in secondary.Tabs.ToList())
        {
            secondary.Tabs.Remove(tab);
            if (PrimaryGroup.TabFor(tab.Document) is { } duplicate)
            {
                // Open on both sides; the left one stays.
                if (selected == tab)
                    selected = duplicate;
                tab.Dispose();
            }
            else
                PrimaryGroup.Tabs.Add(tab);
        }

        SecondaryGroup = null;
        PrimaryGroup.SelectedTab = selected ?? PrimaryGroup.Tabs.FirstOrDefault();
        ActiveGroup = PrimaryGroup;
        ActiveTab?.RequestFocus();
    }

    /// <summary>The other half, creating it if the window is not split yet.</summary>
    private TabGroupViewModel OtherGroup(TabGroupViewModel group)
    {
        if (SecondaryGroup is null)
        {
            var secondary = new TabGroupViewModel();
            _subscriptions.Add(secondary.WhenAnyValue(model => model.SelectedTab).Subscribe(_ => OnSelectionChanged(secondary)));
            SecondaryGroup = secondary;
        }

        return group == PrimaryGroup ? SecondaryGroup! : PrimaryGroup;
    }

    private void CycleTab(int step)
    {
        var group = ActiveGroup;
        if (group.Tabs.Count < 2 || group.SelectedTab is not { } selected)
            return;
        var index = (group.Tabs.IndexOf(selected) + step + group.Tabs.Count) % group.Tabs.Count;
        group.SelectedTab = group.Tabs[index];
        group.SelectedTab.RequestFocus();
    }

    /// <summary>Brings a document's tab forward, preferring one in the active half.</summary>
    private void ShowDocument(DocumentViewModel document)
    {
        var group = ActiveGroup.TabFor(document) is not null
            ? ActiveGroup
            : Groups().FirstOrDefault(candidate => candidate.TabFor(document) is not null);
        if (group?.TabFor(document) is not { } tab)
            return;

        group.SelectedTab = tab;
        ActiveGroup = group;
        tab.RequestFocus();
    }

    private void OnSelectionChanged(TabGroupViewModel group)
    {
        if (group == ActiveGroup)
            UpdateActiveTab();
    }

    private void UpdateActiveTab()
    {
        ActiveTab = ActiveGroup.SelectedTab;
        ActiveDocument = ActiveTab?.Document;
    }

    // --- editing -------------------------------------------------------------------------------

    /// <summary>Runs an Edit menu command on the active editor.</summary>
    /// <param name="action">What to do with the editor.</param>
    /// <param name="fallback">What to do with the document if no editor is on screen.</param>
    private void Edit(Action<AvaloniaEdit.TextEditor> action, Action<DocumentViewModel>? fallback = null)
    {
        if (ActiveTab is not { } tab)
            return;
        if (tab.Editor is { } editor)
        {
            action(editor);
            editor.TextArea.Focus();
        }
        else
            fallback?.Invoke(tab.Document);
    }

    private void SetLanguage(string? id)
    {
        if (ActiveDocument is not { } document)
            return;
        document.LanguageId = id;
        document.LanguageChosen = true;
        foreach (var item in LanguageItems)
            item.IsChecked = item.Id == id;
    }

    private async Task EditTabsAsync()
    {
        if (ActiveDocument is not { } document || TabsDialogRequested is null)
            return;

        if (await TabsDialogRequested(document.Tabs) is not { } result)
            return;

        document.Tabs = result.Settings;
        if (result.MakeDefault)
            _state.Update(state => state with { DefaultTabs = result.Settings.Normalized() });
    }

    // --- searching -----------------------------------------------------------------------------

    /// <inheritdoc/>
    public SearchResult FindNext(SearchQuery query)
    {
        if (ActiveTab?.Editor is not { } editor)
            return new SearchResult(FindOutcome.NoDocument);

        return Searching(() =>
        {
            var match = TextSearch.Find(editor.Document.Text, editor.SelectionStart,
                editor.SelectionStart + editor.SelectionLength, query);
            if (match is not { } found)
                return new SearchResult(FindOutcome.NotFound);

            editor.Select(found.Offset, found.Length);
            Reveal(editor, found.Offset);
            return new SearchResult(found.Wrapped ? FindOutcome.Wrapped : FindOutcome.Found);
        });
    }

    /// <inheritdoc/>
    public SearchResult ReplaceNext(SearchQuery query, string replacement, bool thenFind)
    {
        if (ActiveTab?.Editor is not { } editor)
            return new SearchResult(FindOutcome.NoDocument);

        return Searching(() =>
        {
            var document = editor.Document;
            var text = document.Text;
            var offset = editor.SelectionStart;
            var length = editor.SelectionLength;
            var wrapped = false;

            // The selection is the occurrence to replace when it is one, which is what it is after
            // a Find; otherwise the next occurrence is found and replaced.
            var expanded = TextSearch.ReplacementAt(text, offset, length, query, replacement);
            if (expanded is null)
            {
                if (TextSearch.Find(text, offset, offset + length, query) is not { } found)
                    return new SearchResult(FindOutcome.NotFound);
                (offset, length, wrapped) = (found.Offset, found.Length, found.Wrapped);
                expanded = TextSearch.ReplacementAt(text, offset, length, query, replacement) ?? replacement;
            }

            document.Replace(offset, length, expanded);

            // The caret goes past the new text in the direction of the search, so the next search
            // does not find the replacement itself (replacing "a" with "aa", say).
            editor.Select(query.Backward ? offset : offset + expanded.Length, 0);
            Reveal(editor, offset);

            if (thenFind)
                return FindNext(query);
            return new SearchResult(wrapped ? FindOutcome.Wrapped : FindOutcome.Found);
        });
    }

    /// <inheritdoc/>
    public SearchResult ReplaceAll(SearchQuery query, string replacement, ReplaceScope scope)
    {
        if (ActiveTab?.Editor is not { } editor)
            return new SearchResult(FindOutcome.NoDocument);

        return Searching(() =>
        {
            if (scope == ReplaceScope.Selection)
            {
                var start = editor.SelectionStart;
                var end = start + editor.SelectionLength;
                var count = ReplaceIn(editor.Document, query, replacement, start, end, out var delta);
                // The replaced text stays selected, so a second Replace All in Selection works on
                // the same stretch.
                editor.Select(start, end - start + delta);
                return Counted(count, count > 0 ? 1 : 0);
            }

            var documents = scope == ReplaceScope.AllTabs ? [.. _documents] : new[] { ActiveTab.Document };
            var total = 0;
            var touched = 0;
            foreach (var document in documents)
            {
                var count = ReplaceIn(document.Text, query, replacement, 0, document.Text.TextLength, out _);
                total += count;
                if (count > 0)
                    touched++;
            }

            return Counted(total, touched);
        });

        static SearchResult Counted(int count, int documents) => count == 0
            ? new SearchResult(FindOutcome.NotFound)
            : new SearchResult(FindOutcome.Found, Count: count, Documents: documents);
    }

    /// <summary>Replaces every match between two offsets of one document.</summary>
    /// <param name="delta">How much longer the range is afterward.</param>
    /// <returns>How many were replaced.</returns>
    /// <remarks>
    /// In one update, so a single Undo takes the whole Replace All back. Applied from the last
    /// match to the first, so each edit leaves the offsets of the ones still to come alone.
    /// </remarks>
    private static int ReplaceIn(AvaloniaEdit.Document.TextDocument document, SearchQuery query, string replacement,
        int start, int end, out int delta)
    {
        var edits = TextSearch.ReplaceAll(document.Text, start, end, query, replacement);
        delta = 0;
        if (edits.Count == 0)
            return 0;

        document.BeginUpdate();
        try
        {
            for (var i = edits.Count - 1; i >= 0; i--)
            {
                var edit = edits[i];
                document.Replace(edit.Offset, edit.Length, edit.Replacement);
                delta += edit.Replacement.Length - edit.Length;
            }
        }
        finally
        {
            document.EndUpdate();
        }

        return edits.Count;
    }

    /// <summary>Runs a search, turning a bad or runaway pattern into a result rather than an exception.</summary>
    private static SearchResult Searching(Func<SearchResult> search)
    {
        try
        {
            return search();
        }
        catch (InvalidPatternException ex)
        {
            return new SearchResult(FindOutcome.Invalid, ex.Message);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return new SearchResult(FindOutcome.Invalid);
        }
    }

    /// <summary>Scrolls <paramref name="offset"/> into view.</summary>
    private static void Reveal(AvaloniaEdit.TextEditor editor, int offset)
    {
        var location = editor.Document.GetLocation(offset);
        editor.ScrollTo(location.Line, location.Column);
    }

    // --- changes on disk -----------------------------------------------------------------------

    private void Watch(DocumentViewModel document, string path)
    {
        foreach (var watched in document.WatchPaths(path))
            _watcher.Watch(watched);
    }

    private void Unwatch(DocumentViewModel document, string path)
    {
        foreach (var watched in document.WatchPaths(path))
            _watcher.Unwatch(watched);
    }

    private void OnFileChanged(string path)
    {
        foreach (var document in _documents)
        {
            if (document.Path is not { } own || !document.WatchPaths(own).Contains(path))
                continue;

            var current = DocumentIO.Probe(own);
            if (current is null)
            {
                // Reported once: a document already diverged from a missing file has nothing new
                // to say until the file comes back.
                if (document.Stamp is not null)
                    document.PendingNotice = DiskNotice.Deleted;
            }
            else if (current != document.Stamp)
                document.PendingNotice = DiskNotice.Changed;
        }

        if (IsWindowActive)
            _ = AskAboutDiskChangesAsync();
    }

    /// <summary>Asks about every document whose file has changed, one at a time.</summary>
    private async Task AskAboutDiskChangesAsync()
    {
        if (_noticing)
            return;
        _noticing = true;
        try
        {
            while (_documents.FirstOrDefault(document => document.PendingNotice != DiskNotice.None) is { } document)
            {
                document.PendingNotice = DiskNotice.None;
                if (document.Path is not { } path)
                    continue;

                // Looked at again rather than trusting the notice: a file can be deleted and
                // written back in one save, and the window may have been in the background for
                // all of it.
                var current = DocumentIO.Probe(path);
                if (current is null)
                {
                    // A document already diverged from a missing file has been told.
                    if (document.Stamp is null)
                        continue;
                    document.Diverge(null);
                    ShowDocument(document);
                    if (DeletedNoticeRequested is not null)
                        await DeletedNoticeRequested(document.DisplayName);
                    continue;
                }

                // Changed back, or changed by a save of ours, since it was noticed.
                if (current == document.Stamp)
                    continue;

                ShowDocument(document);
                var reload = ReloadRequested is not null && await ReloadRequested(document.DisplayName, document.IsModified);
                if (!reload)
                {
                    document.Diverge(current);
                    continue;
                }

                try
                {
                    document.Reload(await Task.Run(() => DocumentIO.Load(path)));
                    _logger.ZLogInformation($"reloaded {path}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.ZLogWarning(ex, $"could not reload {path}");
                    ErrorRaised?.Invoke(Strings.OpenFailed(path, ex.Message));
                }
            }
        }
        finally
        {
            _noticing = false;
        }
    }

    private void OnCommandFailed(Exception error)
    {
        _logger.ZLogError(error, $"command failed");
        ErrorRaised?.Invoke(error.Message);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        foreach (var group in Groups())
        {
            foreach (var tab in group.Tabs)
                tab.Dispose();
        }

        foreach (var document in _documents)
            document.Dispose();
    }
}
