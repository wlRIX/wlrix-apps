using Wlrix.Files.Core.Operations;

namespace Wlrix.Files.Core.Portal;

/// <summary>Where each of <c>SaveFiles</c>' names lands in the folder the user chose.</summary>
/// <remarks>
/// <para>
/// The interface is explicit about this being the backend's job: the names come from the
/// application, the folder from the user, and "if the selected folder already contains a file
/// with one of the given names, the portal may prompt or take some other action to construct a
/// unique file name and return that instead". This takes the other action rather than prompting
/// — an application saving eight attachments would otherwise ask eight questions, and the same
/// renaming policy already governs every copy the file manager makes.
/// </para>
/// <para>
/// The answer must have <b>one URI per requested name, in the order asked</b>. An application
/// matches them up by position, so dropping one would silently shift every file after it onto
/// the wrong name.
/// </para>
/// </remarks>
public static class SaveFilesPlan
{
    /// <summary>Names the files, avoiding what is already there and each other.</summary>
    /// <param name="folder">The chosen folder.</param>
    /// <param name="names">
    /// What the application asked for, in its order. A name is taken as a bare filename: one
    /// carrying a path separator is a sandboxed application trying to write outside the folder
    /// the user picked, so only its last component is used.
    /// </param>
    /// <param name="exists">Whether a name is already taken in the folder.</param>
    public static IReadOnlyList<Location> Resolve(
        Location folder,
        IEnumerable<string> names,
        Func<string, bool> exists)
    {
        var chosen = new List<Location>();
        // Names taken during this run as well as before it: two attachments both called
        // `invoice.pdf` are a real case, and checking only the disk would return one URI twice
        // and lose a file.
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var requested in names)
        {
            var name = Sanitize(requested);
            if (exists(name) || claimed.Contains(name))
                name = ConflictNaming.NextFreeName(name, taken => exists(taken) || claimed.Contains(taken));

            claimed.Add(name);
            chosen.Add(folder.Child(name));
        }

        return chosen;
    }

    /// <summary>Reduces an application's name to something that can only land in the folder.</summary>
    /// <remarks>
    /// <c>Location.Child</c> refuses a separator outright, so this is what turns a refusal into
    /// a file saved where the user said. <c>.</c> and <c>..</c> survive that check and name the
    /// folder itself, so they are replaced rather than trimmed.
    /// </remarks>
    private static string Sanitize(string requested)
    {
        var name = requested;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];

        if (name.Length == 0 || name is "." or "..")
            name = "file";

        return name;
    }
}
