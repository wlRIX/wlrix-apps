using Wlrix.Files.Core;
using Wlrix.Files.Core.Portal;

namespace Wlrix.FilePicker.Services;

/// <summary>What pressing the accept button does.</summary>
internal enum AcceptAction
{
    /// <summary>Nothing to accept yet — no selection, or an empty name.</summary>
    None,

    /// <summary>The user aimed at a folder: open it rather than answering with it.</summary>
    Navigate,

    /// <summary>A save that would replace a file. Ask first, then accept.</summary>
    ConfirmOverwrite,

    /// <summary>Answer the portal.</summary>
    Accept,
}

/// <summary>What the accept button should do, and with what.</summary>
/// <param name="Action">Which of the four.</param>
/// <param name="Target">Where to go, for <see cref="AcceptAction.Navigate"/>.</param>
/// <param name="Chosen">
/// What to answer with, for <see cref="AcceptAction.Accept"/> — and for
/// <see cref="AcceptAction.ConfirmOverwrite"/> too, so a confirmed overwrite needs no second
/// decision and cannot reach a different answer than the one the question named.
/// </param>
internal readonly record struct AcceptDecision(
    AcceptAction Action,
    Location? Target = null,
    IReadOnlyList<Location>? Chosen = null)
{
    public static readonly AcceptDecision Nothing = new(AcceptAction.None);
}

/// <summary>
/// The whole of what the accept button means, as a function of what the portal asked and what
/// is on screen.
/// </summary>
/// <remarks>
/// Pure and in one place, for the reason <c>DropPolicy</c> and <c>IconGridMetrics</c> are: every
/// branch here is reached by a gesture, and nothing on this machine can synthesize one. Written
/// this way it is settled by tests instead — which matters more here than in the file manager,
/// because the wrong branch does not draw the wrong thing, it hands an application a file the
/// user did not choose.
/// </remarks>
internal static class AcceptPolicy
{
    /// <summary>Decide what to do.</summary>
    /// <param name="request">What the portal asked for.</param>
    /// <param name="folder">The folder on screen.</param>
    /// <param name="selected">The rows the user has selected, in the listing's order.</param>
    /// <param name="typedName">What is in the name field. Ignored outside a save.</param>
    /// <param name="probe">
    /// What is at a location, or null if nothing is. The one piece of I/O, passed in so the
    /// rest of this is a function of its arguments.
    /// </param>
    public static AcceptDecision Decide(
        FileChooserRequest request,
        Location folder,
        IReadOnlyList<FileEntry> selected,
        string typedName,
        Func<Location, FileKind?> probe)
    {
        return request.Mode switch
        {
            // The folder is the answer; the names came with the question. Every name is
            // answered, in the order asked, because the application matches them up by
            // position -- see SaveFilesPlan.
            FileChooserMode.SaveFiles => new AcceptDecision(
                AcceptAction.Accept,
                Chosen: SaveFilesPlan.Resolve(folder, request.Files, name => probe(folder.Child(name)) is not null)),

            FileChooserMode.Save => Save(folder, selected, typedName, probe),
            _ => Open(request, folder, selected),
        };
    }

    private static AcceptDecision Save(
        Location folder,
        IReadOnlyList<FileEntry> selected,
        string typedName,
        Func<Location, FileKind?> probe)
    {
        var name = typedName.Trim();
        if (name.Length == 0)
        {
            // An empty field with a folder selected is somebody about to save *into* that
            // folder, so accept opens it. With nothing selected there is simply no answer yet.
            return selected is [{ IsDirectory: true } directory]
                ? new AcceptDecision(AcceptAction.Navigate, directory.Location)
                : AcceptDecision.Nothing;
        }

        Location target;
        if (name.StartsWith('/'))
        {
            // A typed absolute path is honored, because somebody who typed one meant it and
            // every other chooser on the system accepts one.
            target = Location.FromLocalPath(name);
        }
        else
        {
            // A relative name keeps only its last component. A chooser is not a shell: the
            // folder on screen is what the user agreed to, and `Location.Child` refuses a
            // separator outright, so the alternative is not a saved file but a failed call.
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name[(slash + 1)..];
            if (name.Length == 0 || name is "." or "..")
                return AcceptDecision.Nothing;
            target = folder.Child(name);
        }

        return probe(target) switch
        {
            // Typing the name of a folder opens it, which is how a path is typed a step at a
            // time. It is also the only reading that cannot destroy something.
            FileKind.Directory => new AcceptDecision(AcceptAction.Navigate, target),
            null => new AcceptDecision(AcceptAction.Accept, Chosen: [target]),
            _ => new AcceptDecision(AcceptAction.ConfirmOverwrite, Chosen: [target]),
        };
    }

    private static AcceptDecision Open(
        FileChooserRequest request,
        Location folder,
        IReadOnlyList<FileEntry> selected)
    {
        if (request.Directory)
        {
            var folders = selected.Where(entry => entry.IsDirectory).Select(entry => entry.Location).ToList();
            // Nothing selected means the folder being looked at, which is what somebody who
            // navigated into it and pressed the button meant.
            if (folders.Count == 0)
                return new AcceptDecision(AcceptAction.Accept, Chosen: [folder]);

            return new AcceptDecision(AcceptAction.Accept, Chosen: Limit(request, folders));
        }

        // One folder and nothing else: open it. This is what makes the button follow the
        // listing rather than needing a double-click for folders and a press for files.
        if (selected is [{ IsDirectory: true } only])
            return new AcceptDecision(AcceptAction.Navigate, only.Location);

        var files = selected.Where(entry => !entry.IsDirectory).Select(entry => entry.Location).ToList();
        return files.Count == 0
            ? AcceptDecision.Nothing
            : new AcceptDecision(AcceptAction.Accept, Chosen: Limit(request, files));
    }

    /// <summary>
    /// Honors <c>multiple</c> even if the listing somehow selected more.
    /// </summary>
    /// <remarks>
    /// The listing is put in single-selection mode when the application did not ask for more,
    /// so this should never have anything to do. It is here because an application that asked
    /// for one file and is handed three is not a cosmetic bug: it will use the first and the
    /// user will believe all three went.
    /// </remarks>
    private static IReadOnlyList<Location> Limit(FileChooserRequest request, List<Location> chosen) =>
        request.Multiple || chosen.Count <= 1 ? chosen : [chosen[0]];
}
