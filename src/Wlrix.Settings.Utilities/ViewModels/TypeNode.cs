// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Utilities.Services;

namespace Wlrix.Settings.Utilities.ViewModels;

/// <summary>A line of the Known Types tree: a media group, or a type within one.</summary>
public sealed class TypeNode : ViewModelBase
{
    private readonly IconCache? _icons;
    private readonly IReadOnlyList<string> _iconNames;
    private Bitmap? _icon;
    private bool _iconRequested;
    private bool _isExpanded;

    private TypeNode(string name, string? type, IconCache? icons, IReadOnlyList<string> iconNames)
    {
        Name = name;
        Type = type;
        _icons = icons;
        _iconNames = iconNames;
    }

    /// <summary>What the tree shows: the media group, or the subtype.</summary>
    public string Name { get; }

    /// <summary>The whole type, or null for a group.</summary>
    public string? Type { get; }

    public ObservableCollection<TypeNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    /// <summary>The type's icon, rendered the first time a row shows it.</summary>
    /// <remarks>
    /// Not up front: there are a thousand types, and a group nobody expands should cost
    /// nothing.
    /// </remarks>
    public Bitmap? Icon
    {
        get
        {
            if (!_iconRequested && _icons is not null)
            {
                _iconRequested = true;
                _ = LoadAsync(_icons);
            }
            return _icon;
        }
    }

    internal static TypeNode Group(string media) => new(media, null, null, []);

    internal static TypeNode ForType(string type, IconCache icons, IReadOnlyList<string> iconNames) =>
        new(type[(type.IndexOf('/') + 1)..], type, icons, iconNames);

    private async Task LoadAsync(IconCache icons)
    {
        var bitmap = await icons.Get(_iconNames).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            _icon = bitmap;
            this.RaisePropertyChanged(nameof(Icon));
        });
    }
}
