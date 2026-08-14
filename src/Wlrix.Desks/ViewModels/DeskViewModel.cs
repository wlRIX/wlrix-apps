using Avalonia;
using ReactiveUI;
using Wlrix.Desks.Models;

namespace Wlrix.Desks.ViewModels;

/// <summary>A window rectangle already scaled into a desk's preview box.</summary>
public sealed record PreviewWindow(double X, double Y, double W, double H);

/// <summary>
/// One desk tile: its name, whether it is the active desk (drives the LED lamp), and the
/// opaque geometry of its windows mapped into a preview box that carries the screen layout's
/// own aspect ratio (<see cref="PreviewWidth"/> × <see cref="PreviewHeight"/>).
/// Id 0 is the Global desk (<see cref="IsGlobal"/>).
/// </summary>
public sealed class DeskViewModel(int id) : ViewModelBase
{
    /// <summary>The preview box the screen layout is fitted into; the box that comes out keeps
    /// the layout's aspect ratio, so a wide multi-monitor desktop gets a wide, shorter tile
    /// rather than a fixed box with dead space above and below the windows.</summary>
    public const double MaxPreviewWidth = 320;
    public const double MaxPreviewHeight = 100;

    private string _name = string.Empty;
    private bool _isActive;
    private bool _isEditing;
    private string _editName = string.Empty;
    private double _previewWidth = MaxPreviewWidth;
    private double _previewHeight = MaxPreviewHeight;
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

    /// <summary>
    /// True while the tile's name is being edited in place — IRIX renamed things inline rather
    /// than through a prompt, so the header swaps its label for a text field.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    /// <summary>The draft the inline editor edits. <see cref="Name"/> stays the compositor's
    /// name until the rename comes back on a snapshot.</summary>
    public string EditName
    {
        get => _editName;
        set => this.RaiseAndSetIfChanged(ref _editName, value);
    }

    public IReadOnlyList<PreviewWindow> PreviewWindows
    {
        get => _previewWindows;
        private set => this.RaiseAndSetIfChanged(ref _previewWindows, value);
    }

    /// <summary>Width of the preview box, sized from the screen layout's aspect ratio.</summary>
    public double PreviewWidth
    {
        get => _previewWidth;
        private set => this.RaiseAndSetIfChanged(ref _previewWidth, value);
    }

    /// <summary>Height of the preview box, sized from the screen layout's aspect ratio.</summary>
    public double PreviewHeight
    {
        get => _previewHeight;
        private set => this.RaiseAndSetIfChanged(ref _previewHeight, value);
    }

    /// <summary>Opens the inline name editor, seeded with the current name.</summary>
    public void BeginEdit()
    {
        EditName = Name;
        IsEditing = true;
    }

    /// <summary>Closes the inline name editor, keeping whatever <see cref="Name"/> holds.</summary>
    public void EndEdit() => IsEditing = false;

    /// <summary>Refreshes this tile from a snapshot. <paramref name="windows"/> are the windows
    /// to draw (the desk's own plus the Global desk's); <paramref name="world"/> is the bounding
    /// box of all outputs.</summary>
    public void Update(DeskInfo info, IReadOnlyList<WindowInfo> windows, Rect world)
    {
        Name = info.Name;
        IsActive = info.Active;

        if (world.Width <= 0 || world.Height <= 0)
        {
            PreviewWindows = [];
            return;
        }

        // Fit the screen layout into the preview budget and take the fitted size as the box
        // itself, so the windows always reach all four edges instead of being letterboxed.
        var scale = Math.Min(MaxPreviewWidth / world.Width, MaxPreviewHeight / world.Height);
        PreviewWidth = Math.Round(world.Width * scale);
        PreviewHeight = Math.Round(world.Height * scale);
        PreviewWindows = Scale(windows, world, scale);
    }

    // Maps world coordinates into the preview box, which shares the world's aspect ratio.
    private static IReadOnlyList<PreviewWindow> Scale(
        IReadOnlyList<WindowInfo> windows, Rect world, double scale)
    {
        var result = new List<PreviewWindow>(windows.Count);
        foreach (var w in windows)
        {
            // Minimized windows are drawn too: the compositor reports the rectangle of their
            // icon in the minimized grid, so they land in the preview's corner exactly where
            // the icons sit on screen.
            result.Add(new PreviewWindow(
                (w.X - world.X) * scale,
                (w.Y - world.Y) * scale,
                Math.Max(1, w.W * scale),
                Math.Max(1, w.H * scale)));
        }

        return result;
    }
}
