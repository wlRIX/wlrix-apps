// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using ReactiveUI;
using Wlrix.Settings.Utilities.Localization;
using Wlrix.Settings.Utilities.Services;

namespace Wlrix.Settings.Utilities.ViewModels;

/// <summary>
/// The Desktop Utilities panel: the Utilities page, the File Types page, and Apply.
/// </summary>
/// <remarks>
/// Nothing is written until Apply, as on KDE's pages and unlike IRIX's panel, which wrote
/// each menu as it changed. Here one choice can rewrite the defaults for dozens of types, and
/// a whole page of pattern edits is worth being able to throw away.
/// </remarks>
public sealed class UtilitiesViewModel : ViewModelBase
{
    private readonly PendingChanges _pending = new();
    private readonly Dictionary<RoleViewModel, IReadOnlyList<string>> _roleTypes = [];
    private Associations? _associations;
    private IconCache? _icons;
    private FileTypesViewModel? _fileTypes;
    private bool _isReady;
    private bool _isApplying;
    private string? _status;
    private int _selectedTab;

    public UtilitiesViewModel() =>
        _pending.Changed += () =>
        {
            this.RaisePropertyChanged(nameof(HasChanges));
            this.RaisePropertyChanged(nameof(CanApply));
        };

    public ObservableCollection<RoleGroupViewModel> RoleGroups { get; } = [];

    public FileTypesViewModel? FileTypes
    {
        get => _fileTypes;
        private set => this.RaiseAndSetIfChanged(ref _fileTypes, value);
    }

    /// <summary>False until the entries and the type database have been read.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set => this.RaiseAndSetIfChanged(ref _isReady, value);
    }

    public bool HasChanges => !_pending.IsEmpty;

    public bool CanApply => HasChanges && !_isApplying;

    public string? Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    /// <summary>Every application, for the chooser behind Other… and Add….</summary>
    public IReadOnlyList<AppChoice> AllApplications =>
        _associations is null ? [] : [.. _associations.Applications.Select(entry => AppChoice.For(entry, _icons))];

    /// <summary>Set by the window: shows the chooser of every application.</summary>
    internal Func<Task<AppChoice?>>? ChooseApplication { get; set; }

    /// <summary>Reads the entries and the database, off the UI thread, then fills both pages.</summary>
    public async Task InitializeAsync()
    {
        var associations = await Task.Run(Associations.ForSession);
        _associations = associations;
        _icons = IconCache.ForSession();

        foreach (var group in UtilityRoles.All.GroupBy(static role => role.Group))
        {
            var roles = group.Select(role =>
            {
                var offered = associations.OfferedFor(role).ToList();
                var current = associations.CurrentFor(role);
                // The default may be something that does not claim the role's type -- picked
                // with Other… earlier, or set by hand -- and the menu still has to show it.
                if (current is not null && offered.All(entry => entry.Id != current) && associations.Find(current) is { } extra)
                    offered.Add(extra);
                var model = new RoleViewModel(role, offered.Select(entry => AppChoice.For(entry, _icons)), current, OnRoleChanged)
                {
                    ChooseOther = _ => ChooseApplication?.Invoke() ?? Task.FromResult<AppChoice?>(null),
                };
                return model;
            }).ToList();
            RoleGroups.Add(new RoleGroupViewModel(Strings.Catalog.Get(group.Key), roles));
        }

        FileTypes = new FileTypesViewModel(associations, _pending, _icons);
        IsReady = true;
    }

    /// <summary>Writes everything pending.</summary>
    public async Task ApplyAsync()
    {
        if (_associations is null || _pending.IsEmpty || _isApplying)
            return;

        _isApplying = true;
        this.RaisePropertyChanged(nameof(CanApply));
        try
        {
            var error = await _associations.ApplyAsync(_pending);
            Status = error is null ? null : Strings.ApplyFailed(error);
            _pending.Clear();
            _roleTypes.Clear();
            foreach (var role in RoleGroups.SelectMany(static group => group.Roles))
                role.Reset(role.Selected, asOriginal: true);
            FileTypes?.Reload();
        }
        finally
        {
            _isApplying = false;
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    /// <summary>Throws away everything pending.</summary>
    public void Reset()
    {
        _pending.Clear();
        _roleTypes.Clear();
        foreach (var role in RoleGroups.SelectMany(static group => group.Roles))
            role.Reset(role.Original, asOriginal: false);
        FileTypes?.Reload();
        Status = null;
    }

    /// <summary>
    /// A menu changed: the role's types get the chosen application first in their order.
    /// </summary>
    /// <remarks>
    /// The order is the rest of each type's current one, so the previous default becomes the
    /// second choice rather than disappearing. Changing the same menu twice starts again from
    /// the disk, and putting it back to what it showed at open undoes it entirely.
    /// </remarks>
    private void OnRoleChanged(RoleViewModel role, AppChoice? choice)
    {
        if (_associations is null)
            return;

        if (role.Role.Kind == RoleKind.Terminal)
        {
            _pending.SetTerminal(choice?.Entry is { } terminal && !ReferenceEquals(choice, role.Original) ? terminal.Id : null);
            return;
        }

        if (_roleTypes.Remove(role, out var previous))
            _pending.RemoveOrders(previous);

        if (choice?.Entry is { } entry && !ReferenceEquals(choice, role.Original))
        {
            var types = role.Role.TypesFor(entry);
            foreach (var type in types)
            {
                var order = _associations.OrderFor(type).Where(id => id != entry.Id).Prepend(entry.Id).ToList();
                _pending.SetOrder(type, order);
            }
            _roleTypes[role] = types;
        }

        FileTypes?.RefreshDetails();
    }
}
