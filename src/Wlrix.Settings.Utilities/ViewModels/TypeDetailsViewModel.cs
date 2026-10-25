// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Common.Desktop;
using Wlrix.Files.Core.Mime;
using Wlrix.Settings.Utilities.Services;

namespace Wlrix.Settings.Utilities.ViewModels;

/// <summary>
/// The right half of the File Types page: one type's patterns, description and applications.
/// </summary>
/// <remarks>
/// Shows the pending state, not the disk's. Every edit goes straight into
/// <see cref="PendingChanges"/>, so leaving a type and coming back finds the edit still there,
/// and Apply writes what is on screen.
/// </remarks>
public sealed class TypeDetailsViewModel : ViewModelBase
{
    private readonly Associations _associations;
    private readonly PendingChanges _pending;
    private readonly IconCache _icons;
    private string _description = string.Empty;
    private string? _selectedPattern;
    private AppChoice? _selectedApp;
    private Bitmap? _icon;
    private bool _loading;

    internal TypeDetailsViewModel(string type, Associations associations, PendingChanges pending, IconCache icons)
    {
        Type = type;
        _associations = associations;
        _pending = pending;
        _icons = icons;
        _ = LoadIconAsync();
        Load();
    }

    public string Type { get; }

    public Bitmap? Icon
    {
        get => _icon;
        private set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    public ObservableCollection<string> Patterns { get; } = [];

    public ObservableCollection<AppChoice> Apps { get; } = [];

    public string? SelectedPattern
    {
        get => _selectedPattern;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPattern, value);
            this.RaisePropertyChanged(nameof(CanRemovePattern));
        }
    }

    public AppChoice? SelectedApp
    {
        get => _selectedApp;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedApp, value);
            this.RaisePropertyChanged(nameof(CanMoveUp));
            this.RaisePropertyChanged(nameof(CanMoveDown));
            this.RaisePropertyChanged(nameof(CanRemoveApp));
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (string.Equals(_description, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _description, value);
            if (_loading)
                return;

            var system = _associations.SystemDatabase.DescriptionFor(Type) ?? string.Empty;
            var trimmed = value.Trim();
            Store(Current() with { Description = trimmed == system || trimmed.Length == 0 ? null : trimmed });
        }
    }

    public bool CanRemovePattern => SelectedPattern is not null;

    public bool CanMoveUp => SelectedApp is not null && Apps.IndexOf(SelectedApp) > 0;

    public bool CanMoveDown => SelectedApp is not null && Apps.IndexOf(SelectedApp) is var i && i >= 0 && i < Apps.Count - 1;

    public bool CanRemoveApp => SelectedApp is not null;

    /// <summary>Whether this is the system's type with changes of the user's to undo.</summary>
    public bool CanRevert =>
        _associations.IsSystemType(Type)
        && (_pending.TypeEdits.ContainsKey(Type)
            || (_associations.UserTypes.Has(Type) && !_pending.TypeDeletions.Contains(Type)));

    public void AddPattern(string pattern)
    {
        if (Patterns.Contains(pattern, StringComparer.Ordinal))
            return;
        Patterns.Add(pattern);
        StorePatterns();
    }

    public void RemovePattern()
    {
        if (SelectedPattern is { } pattern && Patterns.Remove(pattern))
            StorePatterns();
    }

    public void AddApp(DesktopEntry entry)
    {
        var existing = Apps.FirstOrDefault(app => app.Entry?.Id == entry.Id);
        if (existing is null)
        {
            existing = AppChoice.For(entry, _icons);
            Apps.Add(existing);
            StoreOrder();
        }
        SelectedApp = existing;
    }

    public void RemoveApp()
    {
        if (SelectedApp is { } app && Apps.Remove(app))
            StoreOrder();
    }

    public void MoveUp() => Move(-1);

    public void MoveDown() => Move(+1);

    public void Revert()
    {
        _pending.DeleteType(Type);
        Load();
    }

    /// <summary>Reads the type afresh, from the pending changes and then the disk.</summary>
    internal void Load()
    {
        _loading = true;
        try
        {
            var definition = Definition();
            var deleted = _pending.TypeDeletions.Contains(Type);
            var database = deleted ? _associations.SystemDatabase : _associations.Database;

            // A definition of the user's covers what it says and leaves the rest to the system.
            // Without one, the compiled database is the answer, which is also where a package
            // some other tool wrote shows up.
            var patterns = definition is not null
                ? definition.Patterns ?? _associations.SystemDatabase.PatternsFor(Type)
                : database.PatternsFor(Type);
            Patterns.Clear();
            foreach (var pattern in patterns)
                Patterns.Add(pattern);

            Description = (definition is not null
                ? definition.Description ?? _associations.SystemDatabase.DescriptionFor(Type)
                : database.DescriptionFor(Type)) ?? string.Empty;

            var order = _pending.Orders.TryGetValue(Type, out var pending) ? pending : _associations.OrderFor(Type);
            Apps.Clear();
            foreach (var id in order)
            {
                if (_associations.Find(id) is { } entry)
                    Apps.Add(AppChoice.For(entry, _icons));
            }
            SelectedApp = null;
            SelectedPattern = null;
        }
        finally
        {
            _loading = false;
        }
        this.RaisePropertyChanged(nameof(CanRevert));
    }

    /// <summary>The user's definition of the type as things stand, or null if there is none.</summary>
    private UserMimeType? Definition() =>
        _pending.TypeEdits.TryGetValue(Type, out var edit)
            ? edit
            : _pending.TypeDeletions.Contains(Type) ? null : _associations.UserTypes.Read(Type);

    private UserMimeType Current() => Definition() ?? new UserMimeType(Type, null, null);

    private void StorePatterns()
    {
        var system = _associations.SystemDatabase.PatternsFor(Type);
        var isSystem = _associations.IsSystemType(Type);
        // Back to exactly the system's is no override at all, which is what keeps a type the
        // user fiddled with and put back from carrying a package forever.
        IReadOnlyList<string>? patterns = isSystem && Patterns.SequenceEqual(system, StringComparer.Ordinal)
            ? null
            : [.. Patterns];
        Store(Current() with { Patterns = patterns });
    }

    private void Store(UserMimeType definition)
    {
        if (definition is { Patterns: null, Description: null } && _associations.IsSystemType(Type))
        {
            // Nothing left of the user's: a revert if there is a package on disk, and simply
            // no change if there is not.
            if (_associations.UserTypes.Has(Type))
                _pending.DeleteType(Type);
            else if (_pending.TypeEdits.Remove(Type))
                _pending.TypeDeletions.Remove(Type);
        }
        else
        {
            _pending.EditType(definition);
        }
        this.RaisePropertyChanged(nameof(CanRevert));
    }

    private void Move(int by)
    {
        if (SelectedApp is not { } app)
            return;
        var from = Apps.IndexOf(app);
        var to = from + by;
        if (from < 0 || to < 0 || to >= Apps.Count)
            return;
        Apps.Move(from, to);
        StoreOrder();
        SelectedApp = null;
        SelectedApp = app;
    }

    private void StoreOrder() =>
        _pending.SetOrder(Type, [.. Apps.Select(static app => app.Entry!.Id)]);

    private async Task LoadIconAsync()
    {
        var bitmap = await _icons.Get(_associations.Database.IconNamesFor(Type)).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => Icon = bitmap);
    }
}
