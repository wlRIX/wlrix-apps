// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using ReactiveUI;
using Wlrix.IconCatalog.Models;
using Wlrix.IconCatalog.Services;

namespace Wlrix.IconCatalog.ViewModels;

/// <summary>One notebook page: a tab and its icons.</summary>
public sealed class PageViewModel : ReactiveObject
{
    private string _name;
    private bool _hasSelection;
    private bool _hasOneLauncher;

    public PageViewModel(CatalogPage page)
    {
        Id = page.Id;
        IsSystem = page.IsSystem;
        _name = page.Name;
        Selection.CollectionChanged += (_, _) =>
        {
            HasSelection = Selection.Count > 0;
            HasOneLauncher = Selection is [{ Icon.Entry: not null }];
        };
    }

    public string Id { get; }

    /// <summary>Made by the catalog, so it cannot be renamed or removed.</summary>
    public bool IsSystem { get; }

    public string Name
    {
        get => _name;
        private set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public ObservableCollection<IconViewModel> Icons { get; } = [];

    /// <summary>The selected icons, bound to the list's selection.</summary>
    public ObservableCollection<IconViewModel> Selection { get; } = [];

    public bool HasSelection
    {
        get => _hasSelection;
        private set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
    }

    /// <summary>Whether exactly one icon is selected and it is a launcher: what Selected ▸ Edit acts on.</summary>
    public bool HasOneLauncher
    {
        get => _hasOneLauncher;
        private set => this.RaiseAndSetIfChanged(ref _hasOneLauncher, value);
    }

    /// <summary>
    /// Takes a rebuilt page, keeping the icons that are still on it -- and with them what is
    /// selected -- and moving everything into the new order.
    /// </summary>
    public void Update(CatalogPage page, IconImages images, RunningApps running)
    {
        Name = page.Name;

        var existing = Icons.ToDictionary(icon => icon.Key, StringComparer.Ordinal);
        var wanted = new HashSet<string>(page.Icons.Select(icon => icon.Key), StringComparer.Ordinal);

        foreach (var gone in Icons.Where(icon => !wanted.Contains(icon.Key)).ToList())
        {
            Selection.Remove(gone);
            Icons.Remove(gone);
        }

        for (var i = 0; i < page.Icons.Count; i++)
        {
            var icon = page.Icons[i];
            if (existing.TryGetValue(icon.Key, out var model))
            {
                model.Update(icon);
                var at = Icons.IndexOf(model);
                if (at != i)
                    Icons.Move(at, i);
            }
            else
            {
                model = new IconViewModel(icon);
                Icons.Insert(i, model);
            }

            model.Refresh(images, running);
        }
    }
}
