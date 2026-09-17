using Wlrix.Files.Core.Operations;

namespace Wlrix.Files.Core.Dnd;

/// <summary>The modifier keys held when a drop happens.</summary>
/// <remarks>
/// Core's own enum rather than Avalonia's <c>KeyModifiers</c>, because this assembly must not
/// reference a UI toolkit — and because the future portal picker will hand these in from a
/// different one.
/// </remarks>
[Flags]
public enum DropModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4
}

/// <summary>What a drop would do.</summary>
public enum DropAction
{
    /// <summary>Nothing: refused, or a no-op the user would not thank us for performing.</summary>
    None,
    Copy,
    Move,
    /// <summary>A symbolic link — IRIX's "Make Reference".</summary>
    Link
}

/// <summary>What a drop resolved to: an action, and the sources it actually applies to.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Sources">
/// The subset of the dragged locations the action applies to. A move drops the ones already
/// sitting in the target directory, which is the common case of a drag that wandered back to
/// where it started.
/// </param>
public readonly record struct DropPlan(DropAction Action, IReadOnlyList<Location> Sources)
{
    public static DropPlan Nothing { get; } = new(DropAction.None, []);

    public bool IsEmpty => Action == DropAction.None || Sources.Count == 0;
}

/// <summary>
/// Decides what a drop means, from the modifiers and where the two ends live.
/// </summary>
/// <remarks>
/// Pure and in Core rather than in the drop handler, for the same reason
/// <see cref="IconGridMetrics"/> is: nothing on a Wayland session can synthesize a drag, so
/// every part of drag and drop that <i>can</i> be tested without a hand on the mouse has to
/// be somewhere a test can reach. What is left in the view is the gesture plumbing.
///
/// <para>
/// Getting the answer exactly right matters more here than in a toolkit that draws a drag
/// image. The compositor gives no custom drag icon to a client that draws no drag surface —
/// Avalonia among them — so the negotiated effect, shown as the cursor shape, is the entire
/// feedback the user gets about what is going to happen.
/// </para>
/// </remarks>
public static class DropPolicy
{
    /// <summary>Works out what dropping <paramref name="sources"/> on <paramref name="target"/> does.</summary>
    /// <param name="sources">The dragged locations.</param>
    /// <param name="target">The directory being dropped on.</param>
    /// <param name="modifiers">What was held down.</param>
    /// <param name="sameFilesystem">
    /// Whether the sources and the target sit on one filesystem, which is what makes an
    /// unmodified drag a move rather than a copy. A caller with a selection spanning
    /// several devices passes false: copy is the direction that loses nothing.
    /// </param>
    public static DropPlan Decide(
        IReadOnlyList<Location> sources,
        Location target,
        DropModifiers modifiers,
        bool sameFilesystem)
    {
        if (sources.Count == 0)
            return DropPlan.Nothing;

        var action = Intent(modifiers, sameFilesystem);
        var applicable = Applicable(sources, target, action);
        return applicable.Count == 0 ? DropPlan.Nothing : new DropPlan(action, applicable);
    }

    /// <summary>What the modifiers ask for, before anything is checked against the target.</summary>
    /// <remarks>
    /// Control+Shift for a link is what GTK and Qt both use, so a wlRIX window and a foreign
    /// one dragged between behave the same way.
    /// </remarks>
    public static DropAction Intent(DropModifiers modifiers, bool sameFilesystem)
    {
        var control = modifiers.HasFlag(DropModifiers.Control);
        var shift = modifiers.HasFlag(DropModifiers.Shift);

        if (control && shift)
            return DropAction.Link;
        if (control)
            return DropAction.Copy;
        if (shift)
            return DropAction.Move;

        // Unmodified: move on one filesystem, copy across. That matches what the operation
        // engine can do cheaply — a move within a filesystem is a rename — and it matches
        // every other file manager, which is what a user's fingers already expect.
        return sameFilesystem ? DropAction.Move : DropAction.Copy;
    }

    /// <summary>Which of the sources the action can actually be performed on.</summary>
    /// <remarks>
    /// Three things are refused outright, and all three are silent disasters rather than
    /// errors if they are not:
    /// <list type="bullet">
    /// <item>a directory dropped into itself or into its own descendant, which for a move
    /// would relocate a tree inside a piece of itself;</item>
    /// <item>a source dropped onto itself;</item>
    /// <item>a move into the directory the source is already in, which does nothing but is
    /// worth answering as "nothing" rather than performing as a rename onto itself.</item>
    /// </list>
    /// A <i>copy</i> into the source's own directory is kept: that is a duplicate, and the
    /// conflict resolver names it.
    /// </remarks>
    public static IReadOnlyList<Location> Applicable(
        IReadOnlyList<Location> sources, Location target, DropAction action)
    {
        if (action == DropAction.None)
            return [];

        var kept = new List<Location>(sources.Count);
        foreach (var source in sources)
        {
            if (source == target || source.Contains(target))
                continue;
            if (action == DropAction.Move && source.Parent == target)
                continue;
            kept.Add(source);
        }
        return kept;
    }

    /// <summary>Turns a plan into the operation that carries it out.</summary>
    public static FileOperation? ToOperation(DropPlan plan, Location target) => plan.Action switch
    {
        DropAction.Copy => FileOperation.Copy(plan.Sources, target),
        DropAction.Move => FileOperation.Move(plan.Sources, target),
        DropAction.Link => FileOperation.Link(plan.Sources, target),
        _ => null
    };
}
