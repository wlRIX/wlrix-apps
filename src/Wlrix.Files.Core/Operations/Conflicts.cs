namespace Wlrix.Files.Core.Operations;

/// <summary>What to do about a name that is already taken.</summary>
public enum ConflictAction
{
    /// <summary>Replace what is there.</summary>
    Overwrite,
    /// <summary>Leave both alone and move on.</summary>
    Skip,
    /// <summary>Write under a different name.</summary>
    Rename,
    /// <summary>Overwrite only if the source is newer. Skip otherwise.</summary>
    OverwriteIfNewer,
    /// <summary>Abandon the whole operation.</summary>
    Cancel
}

/// <summary>The answer to one conflict.</summary>
/// <param name="Action">What to do.</param>
/// <param name="NewName">The name to use, when the action is <see cref="ConflictAction.Rename"/>.</param>
/// <param name="ApplyToAll">
/// Whether the same answer settles every later conflict in this operation. Cached by the job,
/// so a copy of a thousand files asks once rather than a thousand times.
/// </param>
public readonly record struct ConflictDecision(
    ConflictAction Action,
    string? NewName = null,
    bool ApplyToAll = false)
{
    public static ConflictDecision Overwrite(bool all = false) => new(ConflictAction.Overwrite, ApplyToAll: all);
    public static ConflictDecision Skip(bool all = false) => new(ConflictAction.Skip, ApplyToAll: all);
    public static ConflictDecision Rename(string name) => new(ConflictAction.Rename, name);
    public static ConflictDecision Cancel() => new(ConflictAction.Cancel);
}

/// <summary>What the user needs to know to answer a conflict.</summary>
public sealed record ConflictContext(
    Location Source,
    Location Target,
    long SourceSize,
    long TargetSize,
    DateTimeOffset? SourceModified,
    DateTimeOffset? TargetModified,
    bool IsDirectory);

/// <summary>Decides what to do when a target already exists.</summary>
public interface IConflictResolver
{
    Task<ConflictDecision> ResolveAsync(ConflictContext context, CancellationToken cancellationToken);
}

/// <summary>Answers every conflict the same way, without asking.</summary>
/// <remarks>
/// The non-interactive resolvers, for tests and for operations that were started with an
/// explicit choice already made.
/// </remarks>
public sealed class FixedConflictResolver(ConflictAction action) : IConflictResolver
{
    public static IConflictResolver SkipAll { get; } = new FixedConflictResolver(ConflictAction.Skip);
    public static IConflictResolver OverwriteAll { get; } = new FixedConflictResolver(ConflictAction.Overwrite);
    public static IConflictResolver CancelOnConflict { get; } = new FixedConflictResolver(ConflictAction.Cancel);

    public Task<ConflictDecision> ResolveAsync(ConflictContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ConflictDecision(action, ApplyToAll: true));
}

/// <summary>Renames around every conflict, never asking and never overwriting.</summary>
public sealed class AutoRenameResolver : IConflictResolver
{
    public Task<ConflictDecision> ResolveAsync(ConflictContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ConflictDecision.Rename(ConflictNaming.NextName(context.Target.Name)));
}

/// <summary>How a copy names itself when the original name is taken.</summary>
/// <remarks>
/// One place for the whole policy, so changing the style is a single edit rather than a hunt
/// through the engine. The pattern deliberately matches what a user sees elsewhere on the
/// desktop rather than inventing a third convention.
/// </remarks>
public static class ConflictNaming
{
    /// <summary>
    /// <c>notes.txt</c> becomes <c>notes (copy).txt</c>, then <c>notes (copy 2).txt</c>.
    /// </summary>
    /// <remarks>
    /// The suffix goes before the extension, because a file manager and every program that
    /// opens the result both key off the extension. Applying it to a name that is already a
    /// copy increments rather than nesting, so repeated collisions do not produce
    /// "notes (copy) (copy) (copy).txt".
    /// </remarks>
    public static string NextName(string name)
    {
        // A leading dot is part of the name, not an extension: ".bashrc" has no extension.
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        if (TryParseCopy(stem, out var baseName, out var number))
            return $"{baseName} (copy {number + 1}){extension}";

        return $"{stem} (copy){extension}";
    }

    /// <summary>Finds a name's next free variant in a directory.</summary>
    public static string NextFreeName(string name, Func<string, bool> taken)
    {
        var candidate = name;
        for (var guard = 0; guard < 1000; guard++)
        {
            candidate = NextName(candidate);
            if (!taken(candidate))
                return candidate;
        }
        // A thousand collisions means something is wrong with the caller, not the name.
        return $"{name}.{Guid.NewGuid():N}";
    }

    /// <summary>Recognizes a stem this method produced, so the count carries rather than nests.</summary>
    private static bool TryParseCopy(string stem, out string baseName, out int number)
    {
        baseName = stem;
        number = 1;

        if (!stem.EndsWith(')') )
            return false;
        var open = stem.LastIndexOf('(');
        if (open <= 0 || stem[open - 1] != ' ')
            return false;

        var inner = stem[(open + 1)..^1];
        if (inner == "copy")
        {
            baseName = stem[..(open - 1)];
            number = 1;
            return true;
        }

        if (inner.StartsWith("copy ", StringComparison.Ordinal)
            && int.TryParse(inner[5..], out var parsed) && parsed > 0)
        {
            baseName = stem[..(open - 1)];
            number = parsed;
            return true;
        }

        return false;
    }
}
