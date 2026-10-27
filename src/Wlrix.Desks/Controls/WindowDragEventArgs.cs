using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Desks.ViewModels;

namespace Wlrix.Desks.Controls;

/// <summary>A window being dragged out of a <see cref="DeskPreview"/>.</summary>
/// <param name="pointer">The pointer event behind it; null when the drag was canceled.</param>
/// <param name="window">The window, as the preview it was picked up from drew it.</param>
/// <param name="grabOffset">Where in the window it was picked up, in preview coordinates.</param>
public sealed class WindowDragEventArgs(
    RoutedEvent routedEvent, PointerEventArgs? pointer, PreviewWindow window, Vector grabOffset)
    : RoutedEventArgs(routedEvent)
{
    public PreviewWindow Window { get; } = window;

    public Vector GrabOffset { get; } = grabOffset;

    /// <summary>Whether the drag ended without being let go of: Escape, or the pointer taken
    /// away. Nothing should be moved.</summary>
    public bool Canceled => _pointer is null;

    /// <summary>The pointer carrying the window, which releasing cancels the drag.</summary>
    public IPointer? Pointer => _pointer?.Pointer;

    private readonly PointerEventArgs? _pointer = pointer;

    /// <summary>Where the pointer is, relative to <paramref name="visual"/>.</summary>
    public Point PositionIn(Visual visual) => _pointer?.GetPosition(visual) ?? default;
}
