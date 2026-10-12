// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ImageView.Models;

/// <summary>The committed view transforms, with undo and redo.</summary>
/// <remarks>
/// A step is a whole gesture: one drag, from press to release, or one Center. The positions a
/// drag passes through on the way are never recorded, so Undo goes back to where the drag
/// started rather than a pixel short of where it ended.
/// </remarks>
public sealed class TransformHistory
{
    private readonly Stack<ViewTransform> _undo = new();
    private readonly Stack<ViewTransform> _redo = new();

    public TransformHistory(ViewTransform initial) => Current = initial;

    /// <summary>The transform the last committed step left.</summary>
    public ViewTransform Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records a new step. Returns <c>false</c> if it changes nothing.</summary>
    /// <remarks>
    /// A new step clears the redo stack. Redoing a step that branched from somewhere else would
    /// land on a state that never followed the one in front of the user.
    /// </remarks>
    public bool Commit(ViewTransform next)
    {
        if (next == Current)
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
    public void Reset(ViewTransform initial)
    {
        _undo.Clear();
        _redo.Clear();
        Current = initial;
    }
}
