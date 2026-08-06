using Avalonia.Media.Imaging;
using ReactiveUI;
using Wlrix.SourcePicker.Models;
using Wlrix.SourcePicker.Services;

namespace Wlrix.SourcePicker.ViewModels;

/// <summary>One tile: a source, whether it is chosen, and its live thumbnail.</summary>
public sealed class SourceViewModel : ViewModelBase, IDisposable
{
    private readonly PreviewFeed? _feed;
    private bool _isSelected;
    private WriteableBitmap? _preview;

    public SourceViewModel(Source source)
    {
        Source = source;
        _feed = PreviewFeed.Open(source.Preview);
    }

    public Source Source { get; }

    public string Id => Source.Id;
    public bool IsMonitor => Source.IsMonitor;
    public string Title => Source.Label;

    /// <summary>
    /// The application under a window's title. Empty for a monitor, and for a window whose
    /// title already is the application -- repeating it says nothing.
    /// </summary>
    public string Subtitle =>
        Source.IsMonitor || string.Equals(Source.AppId, Source.Label, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Source.AppId;

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public WriteableBitmap? Preview
    {
        get => _preview;
        private set => this.RaiseAndSetIfChanged(ref _preview, value);
    }

    /// <summary>
    /// Pull the newest frame, if there is one.
    /// </summary>
    /// <remarks>
    /// The feed hands back the same bitmap redrawn in place, so a plain property set would not
    /// look like a change to anything. The notification is raised by hand instead.
    /// </remarks>
    public void Refresh()
    {
        if (_feed?.Read() is not { } bitmap)
        {
            return;
        }

        if (ReferenceEquals(bitmap, Preview))
        {
            this.RaisePropertyChanged(nameof(Preview));
        }
        else
        {
            Preview = bitmap;
        }
    }

    public void Dispose() => _feed?.Dispose();
}
