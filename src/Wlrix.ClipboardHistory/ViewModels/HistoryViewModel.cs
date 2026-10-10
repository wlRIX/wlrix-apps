// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Tmds.DBus.Protocol;
using Wlrix.ClipboardHistory.Localization;
using Wlrix.ClipboardHistory.Models;
using Wlrix.ClipboardHistory.Services;

namespace Wlrix.ClipboardHistory.ViewModels;

/// <summary>
/// Backs the popup: the history, the search over it, and what the two tabs show.
/// </summary>
/// <remarks>
/// This model never changes the history itself. Every action is a request to
/// <c>wlrix-clipboard</c>, and what the popup shows is only ever what the daemon last said —
/// the answer to each request arrives the same way as a copy made in another window, as a
/// <see cref="IClipboardHistory.Changed"/> and a fresh read.
/// </remarks>
public sealed class HistoryViewModel : ViewModelBase, IDisposable
{
    private IClipboardHistory? _history;
    private readonly Dictionary<ulong, EntryViewModel> _byId = [];
    private List<EntryViewModel> _entries = [];
    private string? _search;
    private int _selectedTab;
    private EntryViewModel? _selectedAll;
    private EntryViewModel? _selectedStarred;
    private bool _loaded;
    private bool _unavailable;

    /// <summary>The history, newest first, narrowed by <see cref="Search"/>.</summary>
    public ObservableCollection<EntryViewModel> All { get; } = [];

    /// <summary>The starred entries, newest first, narrowed by <see cref="Search"/>.</summary>
    public ObservableCollection<EntryViewModel> Starred { get; } = [];

    /// <summary>Raised when choosing an entry is done with the popup.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised (UI thread) with a user-facing reason a request did nothing.</summary>
    public event Action<string>? ShowError;

    /// <summary>What to look for. Matches anywhere in a text entry, ignoring case.</summary>
    public string? Search
    {
        get => _search;
        set
        {
            if (_search == value)
                return;
            this.RaiseAndSetIfChanged(ref _search, value);
            Refilter();
        }
    }

    /// <summary>0 for All, 1 for Starred.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    public EntryViewModel? SelectedAll
    {
        get => _selectedAll;
        set => this.RaiseAndSetIfChanged(ref _selectedAll, value);
    }

    public EntryViewModel? SelectedStarred
    {
        get => _selectedStarred;
        set => this.RaiseAndSetIfChanged(ref _selectedStarred, value);
    }

    /// <summary>The selected row on whichever tab is showing.</summary>
    public EntryViewModel? Selected => SelectedTab == 0 ? SelectedAll : SelectedStarred;

    /// <summary>What the All tab says instead of a list, or null when it has one.</summary>
    public string? AllMessage => Message(All, Strings.Empty);

    /// <summary>What the Starred tab says instead of a list, or null when it has one.</summary>
    public string? StarredMessage => Message(Starred, Strings.EmptyStarred);

    /// <summary>Whether there is anything Clear would delete.</summary>
    public bool CanClear => _entries.Any(entry => !entry.Starred);

    /// <summary>
    /// Connect to the daemon and read the history.
    /// </summary>
    /// <param name="connect">How to reach the daemon; a fake one in the tests.</param>
    public async Task ConnectAsync(Func<Task<IClipboardHistory>> connect)
    {
        try
        {
            _history = await connect();
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException
                                       or System.Net.Sockets.SocketException)
        {
            SetUnavailable();
            return;
        }

        _history.Changed += () => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        await LoadAsync();
    }

    /// <summary>Read the history afresh.</summary>
    internal async Task LoadAsync()
    {
        if (_history is not { } history)
            return;
        try
        {
            var entries = await history.GetHistoryAsync();
            var active = await history.GetActiveAsync();
            Apply(entries, active);
        }
        catch (DBusExceptionBase)
        {
            // Not on the bus: not started, or gone since the popup opened.
            SetUnavailable();
        }
    }

    /// <summary>Show <paramref name="entries"/>, keeping every row that is still in it.</summary>
    internal void Apply(IReadOnlyList<ClipboardEntry> entries, ulong? active)
    {
        _loaded = true;
        _unavailable = false;
        var kept = new List<EntryViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            if (_byId.TryGetValue(entry.Id, out var row))
                row.Update(entry);
            else
                _byId[entry.Id] = row = new EntryViewModel(this, entry);
            row.IsActive = entry.Id == active;
            kept.Add(row);
        }

        foreach (var id in _byId.Keys.Except(entries.Select(entry => entry.Id)).ToList())
            _byId.Remove(id);
        _entries = kept;
        Refilter();
    }

    /// <summary>Put an entry on the clipboard, and close: choosing is what the popup is for.</summary>
    internal async Task ActivateAsync(EntryViewModel entry)
    {
        if (await RunAsync(history => history.ActivateAsync(entry.Id)))
            CloseRequested?.Invoke();
    }

    internal Task RemoveAsync(EntryViewModel entry) =>
        RunAsync(history => history.RemoveAsync(entry.Id));

    internal Task SetStarredAsync(EntryViewModel entry, bool starred) =>
        RunAsync(history => history.SetStarredAsync(entry.Id, starred));

    internal Task ClearAsync() => RunAsync(history => history.ClearAsync());

    /// <summary>Open one row's editor, closing any other: two half-made edits is one too many.</summary>
    internal void BeginEdit(EntryViewModel entry)
    {
        foreach (var other in _entries.Where(other => other != entry))
            other.IsEditing = false;
        entry.EditText = entry.Text;
        entry.IsEditing = true;
    }

    /// <summary>Close any open editor, saying whether there was one.</summary>
    internal bool CancelEdits()
    {
        var any = false;
        foreach (var entry in _entries.Where(entry => entry.IsEditing))
        {
            entry.IsEditing = false;
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Save an edit. An edit down to nothing is a deletion, which is what the daemon would ask
    /// for anyway, so it is made one here rather than refused.
    /// </summary>
    internal async Task SaveEditAsync(EntryViewModel entry)
    {
        var text = entry.EditText;
        entry.IsEditing = false;
        if (text == entry.Text)
            return;
        if (text.Length == 0)
            await RemoveAsync(entry);
        else
            await RunAsync(history => history.SetTextAsync(entry.Id, text));
    }

    /// <summary>Make one request, reporting a refusal. True when it was accepted.</summary>
    private async Task<bool> RunAsync(Func<IClipboardHistory, Task> request)
    {
        if (_history is not { } history)
            return false;
        try
        {
            await request(history);
            return true;
        }
        catch (DBusExceptionBase ex)
        {
            ShowError?.Invoke(Strings.Failed(ex.Message));
            return false;
        }
    }

    private void SetUnavailable()
    {
        _loaded = true;
        _unavailable = true;
        _entries = [];
        _byId.Clear();
        Refilter();
    }

    /// <summary>Rebuild both lists from the history and the search, keeping the selections.</summary>
    private void Refilter()
    {
        var selectedAll = SelectedAll?.Id;
        var selectedStarred = SelectedStarred?.Id;

        Replace(All, _entries.Where(entry => Matches(entry)));
        Replace(Starred, _entries.Where(entry => entry.Starred && Matches(entry)));

        // The first row is selected when nothing else is, so Enter on a freshly opened popup
        // pastes what is at the top -- the same as pressing the key twice in KDE.
        SelectedAll = All.FirstOrDefault(entry => entry.Id == selectedAll) ?? All.FirstOrDefault();
        SelectedStarred = Starred.FirstOrDefault(entry => entry.Id == selectedStarred) ?? Starred.FirstOrDefault();

        this.RaisePropertyChanged(nameof(AllMessage));
        this.RaisePropertyChanged(nameof(StarredMessage));
        this.RaisePropertyChanged(nameof(CanClear));
    }

    private bool Matches(EntryViewModel entry) =>
        string.IsNullOrWhiteSpace(Search) || (entry.IsText && EntryText.Matches(entry.Text, Search));

    private static void Replace(ObservableCollection<EntryViewModel> list, IEnumerable<EntryViewModel> entries)
    {
        var wanted = entries.ToList();
        if (list.SequenceEqual(wanted))
            return;
        list.Clear();
        foreach (var entry in wanted)
            list.Add(entry);
    }

    /// <summary>What a tab says when its list is empty, or null when it is not.</summary>
    private string? Message(ICollection<EntryViewModel> list, string empty)
    {
        if (!_loaded)
            return null;
        if (_unavailable)
            return Strings.NotRunning;
        if (list.Count > 0)
            return null;
        return string.IsNullOrWhiteSpace(Search) ? empty : Strings.NoMatches;
    }

    public void Dispose()
    {
        _history?.Dispose();
        _history = null;
    }
}
