// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using ReactiveUI;

namespace Wlrix.Writer.ViewModels;

/// <summary>One half of the window: a strip of tabs and the editor under it.</summary>
/// <remarks>An unsplit window has one of these; a split window has two, side by side.</remarks>
public sealed class TabGroupViewModel : ViewModelBase
{
    private EditorTabViewModel? _selectedTab;
    private bool _isActive;

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = [];

    public EditorTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    /// <summary>Whether this is the half the menus act on: the one that last had the focus.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    /// <summary>This group's tab for <paramref name="document"/>, if it has one.</summary>
    public EditorTabViewModel? TabFor(DocumentViewModel document) =>
        Tabs.FirstOrDefault(tab => tab.Document == document);
}
