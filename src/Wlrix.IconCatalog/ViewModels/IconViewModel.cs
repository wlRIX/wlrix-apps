// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media.Imaging;
using ReactiveUI;
using Wlrix.IconCatalog.Models;
using Wlrix.IconCatalog.Services;

namespace Wlrix.IconCatalog.ViewModels;

/// <summary>One icon on a page.</summary>
public sealed class IconViewModel : ReactiveObject
{
    private CatalogIcon _icon;
    private bool _isRunning;
    private string? _drawn;
    private Bitmap? _rest, _hover, _selected, _selectedInactive;

    public IconViewModel(CatalogIcon icon) => _icon = icon;

    public CatalogIcon Icon => _icon;

    public string Key => _icon.Key;

    public string Name => _icon.Name;

    /// <summary>What the status line says while the pointer is over this icon.</summary>
    public string? Comment => _icon.Comment;

    public bool IsRunning => _isRunning;

    public Bitmap? Rest { get => _rest; private set => this.RaiseAndSetIfChanged(ref _rest, value); }
    public Bitmap? Hover { get => _hover; private set => this.RaiseAndSetIfChanged(ref _hover, value); }
    public Bitmap? Selected { get => _selected; private set => this.RaiseAndSetIfChanged(ref _selected, value); }
    public Bitmap? SelectedInactive { get => _selectedInactive; private set => this.RaiseAndSetIfChanged(ref _selectedInactive, value); }

    /// <summary>Takes a rescan's version of this icon; its label or artwork may have changed.</summary>
    public void Update(CatalogIcon icon)
    {
        var renamed = icon.Name != _icon.Name;
        var commented = icon.Comment != _icon.Comment;
        _icon = icon;
        if (renamed)
            this.RaisePropertyChanged(nameof(Name));
        if (commented)
            this.RaisePropertyChanged(nameof(Comment));
    }

    /// <summary>Draws the icon for whether it is running, if that is not what is showing already.</summary>
    /// <param name="force">Draw even if nothing seems to have changed: the scheme's colors have.</param>
    public void Refresh(IconImages images, RunningApps running, bool force = false)
    {
        var isRunning = running.IsRunning(_icon);
        if (isRunning != _isRunning)
        {
            _isRunning = isRunning;
            this.RaisePropertyChanged(nameof(IsRunning));
        }

        var key = IconImages.KeyOf(_icon, isRunning);
        if (!force && key == _drawn)
            return;
        _drawn = key;

        images.Request(_icon, isRunning, bitmaps =>
        {
            // A later request may have overtaken this one: only the newest is shown.
            if (_drawn != key)
                return;
            Rest = bitmaps.Rest;
            Hover = bitmaps.Hover;
            Selected = bitmaps.Selected;
            SelectedInactive = bitmaps.SelectedInactive;
        });
    }
}
