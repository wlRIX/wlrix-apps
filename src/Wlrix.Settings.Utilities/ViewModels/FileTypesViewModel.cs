// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using ReactiveUI;
using Wlrix.Files.Core.Mime;
using Wlrix.Settings.Utilities.Localization;
using Wlrix.Settings.Utilities.Services;

namespace Wlrix.Settings.Utilities.ViewModels;

/// <summary>
/// The File Types page: the known types as a tree, and the one selected.
/// </summary>
/// <remarks>
/// After KDE's File Associations page, without its Embedding tab, which is about KParts and
/// has nothing to say on a desktop without them.
/// </remarks>
public sealed class FileTypesViewModel : ViewModelBase
{
    private readonly Associations _associations;
    private readonly PendingChanges _pending;
    private readonly IconCache _icons;
    private string _search = string.Empty;
    private TypeNode? _selectedNode;
    private TypeDetailsViewModel? _details;

    internal FileTypesViewModel(Associations associations, PendingChanges pending, IconCache icons)
    {
        _associations = associations;
        _pending = pending;
        _icons = icons;
        Rebuild();
    }

    public ObservableCollection<TypeNode> Groups { get; } = [];

    public string Search
    {
        get => _search;
        set
        {
            if (string.Equals(_search, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _search, value);
            Reload();
        }
    }

    public TypeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            Details = value?.Type is { } type ? new TypeDetailsViewModel(type, _associations, _pending, _icons) : null;
            this.RaisePropertyChanged(nameof(CanRemoveType));
        }
    }

    /// <summary>The selected type, or null while a group or nothing is selected.</summary>
    public TypeDetailsViewModel? Details
    {
        get => _details;
        private set
        {
            this.RaiseAndSetIfChanged(ref _details, value);
            this.RaisePropertyChanged(nameof(HasDetails));
        }
    }

    public bool HasDetails => Details is not null;

    /// <summary>Only a type the user made can be removed. The system's can only be reverted.</summary>
    public bool CanRemoveType => SelectedNode?.Type is { } type && !_associations.IsSystemType(type);

    /// <summary>The media groups a new type may go in: the ones the database already has.</summary>
    public IReadOnlyList<string> MediaGroups =>
        [.. _associations.Database.AllTypes().Select(static type => type[..type.IndexOf('/')]).Distinct(StringComparer.Ordinal)];

    /// <summary>Adds a type of the user's own.</summary>
    /// <returns>Null on success, otherwise why not, for the status line.</returns>
    public string? AddType(string type)
    {
        type = type.Trim().ToLowerInvariant();
        var slash = type.IndexOf('/');
        if (slash <= 0 || slash == type.Length - 1 || type.IndexOf('/', slash + 1) >= 0
            || type.Any(static c => char.IsWhiteSpace(c) || c is ';' or '=' or ':' or '"' or '\\')
            || !MediaGroups.Contains(type[..slash], StringComparer.Ordinal))
        {
            return Strings.InvalidType(type);
        }
        if (AllTypes().Contains(type, StringComparer.Ordinal))
            return Strings.TypeExists(type);

        _pending.EditType(new UserMimeType(type, [], null));
        _search = string.Empty;
        this.RaisePropertyChanged(nameof(Search));
        Rebuild();
        SelectedNode = Groups.SelectMany(static group => group.Children).FirstOrDefault(node => node.Type == type);
        return null;
    }

    public void RemoveType()
    {
        if (!CanRemoveType || SelectedNode?.Type is not { } type)
            return;

        // One the user has not applied yet just stops being pending; one on disk is deleted.
        if (_associations.UserTypes.Has(type))
            _pending.DeleteType(type);
        else
            _pending.TypeEdits.Remove(type);
        _pending.RemoveOrders([type]);
        Rebuild();
    }

    /// <summary>Reads everything afresh, after Apply or Reset.</summary>
    internal void Reload()
    {
        var selected = SelectedNode?.Type;
        Rebuild();
        SelectedNode = Groups.SelectMany(static group => group.Children).FirstOrDefault(node => node.Type == selected);
    }

    /// <summary>Rereads the selected type, after the Utilities page changed its order.</summary>
    internal void RefreshDetails() => Details?.Load();

    private IEnumerable<string> AllTypes() =>
        _associations.Database.AllTypes()
            .Concat(_pending.TypeEdits.Keys)
            .Where(type => !(_pending.TypeDeletions.Contains(type) && !_associations.IsSystemType(type)))
            .Distinct(StringComparer.Ordinal);

    private bool Matches(string type)
    {
        if (_search.Length == 0)
            return true;
        if (type.Contains(_search, StringComparison.OrdinalIgnoreCase))
            return true;
        var patterns = _pending.TypeEdits.TryGetValue(type, out var edit) && edit.Patterns is { } edited
            ? edited
            : _associations.Database.PatternsFor(type);
        return patterns.Any(pattern => pattern.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        var expanded = Groups.Where(static group => group.IsExpanded).Select(static group => group.Name).ToHashSet(StringComparer.Ordinal);
        var filtering = _search.Length > 0;

        Groups.Clear();
        foreach (var media in AllTypes()
                     .Where(Matches)
                     .Order(StringComparer.Ordinal)
                     .GroupBy(static type => type[..type.IndexOf('/')], StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var group = TypeNode.Group(media.Key);
            foreach (var type in media)
                group.Children.Add(TypeNode.ForType(type, _icons, _associations.Database.IconNamesFor(type)));
            // A search opens every group it found something in, since a match inside a closed
            // group looks exactly like no match.
            group.IsExpanded = filtering || expanded.Contains(group.Name);
            Groups.Add(group);
        }
    }
}
