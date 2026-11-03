using ReactiveUI;
using Wlrix.Iconbox.Models;

namespace Wlrix.Iconbox.ViewModels;

/// <summary>One window's tile.</summary>
public sealed class TileViewModel : ReactiveObject
{
    private string _title = string.Empty;
    private string? _imagePath;
    private bool _minimized;
    private bool _activated;

    public TileViewModel(long id) => Id = id;

    /// <summary>The feed's number for the window, which commands are addressed by.</summary>
    public long Id { get; }

    /// <summary>The window's title, drawn under its picture.</summary>
    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    /// <summary>The picture, or null when not even <c>default.png</c> is installed.</summary>
    public string? ImagePath
    {
        get => _imagePath;
        private set => this.RaiseAndSetIfChanged(ref _imagePath, value);
    }

    /// <summary>Minimized: drawn as the compositor's raised tile. Otherwise flat.</summary>
    public bool Minimized
    {
        get => _minimized;
        private set => this.RaiseAndSetIfChanged(ref _minimized, value);
    }

    /// <summary>Has the keyboard: outlined.</summary>
    public bool Activated
    {
        get => _activated;
        private set => this.RaiseAndSetIfChanged(ref _activated, value);
    }

    /// <summary>Take on what the compositor last said about the window.</summary>
    public void Update(ToplevelInfo window, string? imagePath)
    {
        Title = window.Title;
        ImagePath = imagePath;
        Minimized = window.Minimized;
        Activated = window.Activated;
    }
}
