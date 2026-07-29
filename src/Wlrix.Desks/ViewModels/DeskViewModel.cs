using Avalonia;
using ReactiveUI;
using Wlrix.Desks.Models;

namespace Wlrix.Desks.ViewModels;

/// <summary>A window rectangle already scaled into a desk's fixed-size preview box.</summary>
public sealed record PreviewWindow(double X, double Y, double W, double H);

/// <summary>
/// One desk tile: its name, whether it is the active desk (drives the LED lamp), and the
/// opaque geometry of its windows mapped into a fixed <see cref="PreviewWidth"/> ×
/// <see cref="PreviewHeight"/> box. Id 0 is the Global desk (<see cref="IsGlobal"/>).
/// </summary>
public sealed class DeskViewModel(int id) : ViewModelBase
{
    public const double PreviewWidth = 160;
    public const double PreviewHeight = 100;

    private string _name = string.Empty;
    private bool _isActive;
    private IReadOnlyList<PreviewWindow> _previewWindows = [];

    public int Id { get; } = id;

    public bool IsGlobal => Id == 0;

    public string Name
    {
        get => _name;
        private set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    public IReadOnlyList<PreviewWindow> PreviewWindows
    {
        get => _previewWindows;
        private set => this.RaiseAndSetIfChanged(ref _previewWindows, value);
    }

    /// <summary>Refreshes this tile from a snapshot. <paramref name="windows"/> are the windows
    /// to draw (the desk's own plus the Global desk's); <paramref name="world"/> is the bounding
    /// box of all outputs.</summary>
    public void Update(DeskInfo info, IReadOnlyList<WindowInfo> windows, Rect world)
    {
        Name = info.Name;
        IsActive = info.Active;
        PreviewWindows = Scale(windows, world);
    }

    // Aspect-preserving fit of world coordinates into the preview box, centred.
    private static IReadOnlyList<PreviewWindow> Scale(IReadOnlyList<WindowInfo> windows, Rect world)
    {
        if (world.Width <= 0 || world.Height <= 0)
            return [];

        var scale = Math.Min(PreviewWidth / world.Width, PreviewHeight / world.Height);
        var offsetX = (PreviewWidth - world.Width * scale) / 2;
        var offsetY = (PreviewHeight - world.Height * scale) / 2;

        var result = new List<PreviewWindow>(windows.Count);
        foreach (var w in windows)
        {
            // Minimized windows are drawn too: the compositor reports the rectangle of their
            // icon in the minimized grid, so they land in the preview's corner exactly where
            // the icons sit on screen.
            result.Add(new PreviewWindow(
                (w.X - world.X) * scale + offsetX,
                (w.Y - world.Y) * scale + offsetY,
                Math.Max(1, w.W * scale),
                Math.Max(1, w.H * scale)));
        }

        return result;
    }
}
