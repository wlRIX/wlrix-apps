// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ImageView.Models;

/// <summary>The committed states of the image, with undo and redo.</summary>
/// <remarks>
/// A step is a whole gesture: one drag in the view or on a thumbwheel, from press to release, or
/// one Center or field entry. The values a drag passes through on the way are never recorded, so
/// Undo goes back to where the drag started rather than a pixel short of where it ended.
/// </remarks>
public sealed class EditHistory<T> where T : struct, IEquatable<T>
{
    private readonly Stack<T> _undo = new();
    private readonly Stack<T> _redo = new();

    public EditHistory(T initial) => Current = initial;

    /// <summary>The state the last committed step left.</summary>
    public T Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records a new step. Returns <c>false</c> if it changes nothing.</summary>
    /// <remarks>
    /// A new step clears the redo stack. Redoing a step that branched from somewhere else would
    /// land on a state that never followed the one in front of the user.
    /// </remarks>
    public bool Commit(T next)
    {
        if (next.Equals(Current))
            return false;

        _undo.Push(Current);
        _redo.Clear();
        Current = next;
        return true;
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var previous))
            return false;

        _redo.Push(Current);
        Current = previous;
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var next))
            return false;

        _undo.Push(Current);
        Current = next;
        return true;
    }

    /// <summary>Forgets every step and starts again from <paramref name="initial"/>.</summary>
    public void Reset(T initial)
    {
        _undo.Clear();
        _redo.Clear();
        Current = initial;
    }
}
