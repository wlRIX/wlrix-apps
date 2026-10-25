// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using ReactiveUI;
using Wlrix.Settings.Utilities.Localization;
using Wlrix.Settings.Utilities.Services;

namespace Wlrix.Settings.Utilities.ViewModels;

/// <summary>One row of the Utilities page: a label and its option menu.</summary>
public sealed class RoleViewModel : ViewModelBase
{
    private readonly Action<RoleViewModel, AppChoice?> _changed;
    private AppChoice? _selected;
    private bool _quiet;

    internal RoleViewModel(UtilityRole role, IEnumerable<AppChoice> choices, string? current, Action<RoleViewModel, AppChoice?> changed)
    {
        Role = role;
        _changed = changed;
        Choices = [.. choices, AppChoice.Other()];
        Original = current is null ? null : Choices.FirstOrDefault(choice => choice.Entry?.Id == current);
        if (Original is null)
        {
            // An option menu always shows something, and with nothing selected the theme's
            // shows its first item -- which would read as a choice that was never made.
            Original = AppChoice.None();
            Choices.Insert(0, Original);
        }
        _selected = Original;
    }

    internal UtilityRole Role { get; }

    public string Label => Strings.Catalog.Get(Role.Key);

    public ObservableCollection<AppChoice> Choices { get; }

    /// <summary>What was chosen when the panel opened or last applied.</summary>
    public AppChoice? Original { get; private set; }

    /// <summary>Raised when Other… is picked; the window answers with an application or null.</summary>
    internal Func<RoleViewModel, Task<AppChoice?>>? ChooseOther { get; set; }

    public AppChoice? Selected
    {
        get => _selected;
        set
        {
            if (value is { IsOther: true })
            {
                // Other… is a command, not a value: the menu goes back to what it showed
                // until the chooser answers.
                _ = PickOtherAsync();
                this.RaisePropertyChanged();
                return;
            }
            if (ReferenceEquals(value, _selected))
                return;
            this.RaiseAndSetIfChanged(ref _selected, value);
            if (!_quiet)
                _changed(this, value);
        }
    }

    /// <summary>Sets the choice without counting it as the user's.</summary>
    internal void Reset(AppChoice? choice, bool asOriginal)
    {
        _quiet = true;
        try
        {
            if (asOriginal)
                Original = choice;
            Selected = choice;
        }
        finally
        {
            _quiet = false;
        }
    }

    private async Task PickOtherAsync()
    {
        if (ChooseOther is null || await ChooseOther(this) is not { Entry: { } entry } picked)
            return;

        var existing = Choices.FirstOrDefault(choice => choice.Entry?.Id == entry.Id);
        if (existing is null)
        {
            existing = picked;
            Choices.Insert(Choices.Count - 1, existing);
        }
        Selected = existing;
    }
}

/// <summary>A heading on the Utilities page and the rows under it.</summary>
public sealed class RoleGroupViewModel(string heading, IReadOnlyList<RoleViewModel> roles)
{
    public string Heading { get; } = heading;

    public IReadOnlyList<RoleViewModel> Roles { get; } = roles;
}
