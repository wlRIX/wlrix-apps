// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive;
using Avalonia.Media.Imaging;
using ReactiveUI;
using Wlrix.ClipboardHistory.Localization;
using Wlrix.ClipboardHistory.Models;

namespace Wlrix.ClipboardHistory.ViewModels;

/// <summary>
/// One row of the history.
/// </summary>
/// <remarks>
/// Kept across refreshes and updated in place, matched by id, so that a row being edited or a
/// thumbnail already decoded survives the history changing underneath it — which it does every
/// time anything is copied while the popup is open.
/// </remarks>
public sealed class EntryViewModel : ViewModelBase
{
    /// <summary>The height a thumbnail is decoded at: what the row shows, and no more.</summary>
    private const int ThumbnailHeight = 64;

    private readonly HistoryViewModel _owner;
    private ClipboardEntry _entry;
    private (string Line, bool More) _preview;
    private bool _isActive;
    private bool _isEditing;
    private string _editText = string.Empty;
    private Bitmap? _thumbnail;
    private bool _thumbnailTried;
    private string? _imageCaption;

    internal EntryViewModel(HistoryViewModel owner, ClipboardEntry entry)
    {
        _owner = owner;
        _entry = entry;
        _preview = EntryText.Preview(entry.Text);

        ActivateCommand = ReactiveCommand.CreateFromTask(() => _owner.ActivateAsync(this));
        RemoveCommand = ReactiveCommand.CreateFromTask(() => _owner.RemoveAsync(this));
        StarCommand = ReactiveCommand.CreateFromTask(() => _owner.SetStarredAsync(this, !Starred));
        EditCommand = ReactiveCommand.Create(() => _owner.BeginEdit(this));
        SaveCommand = ReactiveCommand.CreateFromTask(() => _owner.SaveEditAsync(this));
        CancelEditCommand = ReactiveCommand.Create(() => { IsEditing = false; });
    }

    public ulong Id => _entry.Id;

    /// <summary>The whole text, for a text entry.</summary>
    public string Text => _entry.Text;

    public bool IsText => _entry.Kind == EntryKind.Text;

    public bool IsImage => _entry.Kind == EntryKind.Image;

    /// <summary>The first line of a text entry, as the row shows it.</summary>
    public string Line => _preview.Line;

    /// <summary>Whether there is more to a text entry than <see cref="Line"/>: the row's ↵.</summary>
    public bool More => _preview.More;

    /// <summary>The ↵ the row shows after <see cref="Line"/> when there is <see cref="More"/>.</summary>
    public string MoreMarker => More ? "\u00a0↵" : string.Empty;

    public bool Starred => _entry.Starred;

    /// <summary>Whether this is what the clipboard holds right now.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    /// <summary>Whether the row is showing its editor rather than its preview.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    /// <summary>The text in the editor, which becomes the entry's on Save.</summary>
    public string EditText
    {
        get => _editText;
        set => this.RaiseAndSetIfChanged(ref _editText, value);
    }

    /// <summary>A small copy of an image entry, decoded the first time a row asks for it.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!IsImage || _thumbnailTried)
                return _thumbnail;
            _thumbnailTried = true;
            try
            {
                using var file = File.OpenRead(_entry.ImagePath);
                // Only ever shrunk: an icon-sized image decoded "to" 64 pixels would be blown up
                // into a blur.
                _thumbnail = ImageInfo.PngSize(_entry.ImagePath) is { Height: <= ThumbnailHeight }
                    ? new Bitmap(file)
                    : Bitmap.DecodeToHeight(file, ThumbnailHeight);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or InvalidOperationException or NotSupportedException)
            {
                // Gone from disk, or not something Skia can read. The caption still says what
                // the entry is, and putting it back on the clipboard does not need a picture.
                _thumbnail = null;
            }

            return _thumbnail;
        }
    }

    /// <summary>What an image entry is, with its size when that can be read cheaply.</summary>
    public string ImageCaption =>
        _imageCaption ??= ImageInfo.PngSize(_entry.ImagePath) is var (width, height)
            ? Strings.ImageCaption(width, height)
            : Strings.Image;

    public ReactiveCommand<Unit, Unit> ActivateCommand { get; }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public ReactiveCommand<Unit, Unit> StarCommand { get; }

    public ReactiveCommand<Unit, Unit> EditCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }

    /// <summary>Take on what the daemon now says about this entry.</summary>
    internal void Update(ClipboardEntry entry)
    {
        var previous = _entry;
        _entry = entry;
        if (previous.Text != entry.Text)
        {
            _preview = EntryText.Preview(entry.Text);
            this.RaisePropertyChanged(nameof(Text));
            this.RaisePropertyChanged(nameof(Line));
            this.RaisePropertyChanged(nameof(More));
            this.RaisePropertyChanged(nameof(MoreMarker));
        }

        if (previous.Starred != entry.Starred)
            this.RaisePropertyChanged(nameof(Starred));
    }
}
