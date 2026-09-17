using Avalonia.Input;
using Avalonia.Platform.Storage;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Dnd;

namespace Wlrix.Files.Views;

/// <summary>Reading a drag: what it carries, what is held down, and what to answer.</summary>
/// <remarks>
/// Shared because both the listing and the places rail take drops, and the two must agree
/// exactly. A rail that read the modifiers differently from the listing beside it would be a
/// bug nobody would think to look for.
/// </remarks>
internal static class DragTransfer
{
    /// <summary>The locations a drag is carrying, as far as we can make sense of them.</summary>
    /// <remarks>
    /// Files only. A foreign application offering nothing but text is not offering something
    /// this window can file away, and guessing that a string is a path would be a good way to
    /// copy the wrong thing.
    /// </remarks>
    public static IReadOnlyList<Location> SourcesOf(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files)
            return [];

        var sources = new List<Location>();
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
                sources.Add(Location.FromLocalPath(path));
        }
        return sources;
    }

    public static DropModifiers Modifiers(DragEventArgs e)
    {
        var modifiers = DropModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            modifiers |= DropModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            modifiers |= DropModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            modifiers |= DropModifiers.Alt;
        return modifiers;
    }

    public static DragDropEffects Effects(DropAction action) => action switch
    {
        DropAction.Copy => DragDropEffects.Copy,
        DropAction.Move => DragDropEffects.Move,
        DropAction.Link => DragDropEffects.Link,
        _ => DragDropEffects.None
    };
}
