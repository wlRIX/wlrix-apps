// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Wlrix.Common.Desktop;

namespace Wlrix.IconCatalog.Models;

/// <summary>What the Edit dialog shows and changes of a launcher.</summary>
/// <param name="Name">The label, in the display language.</param>
/// <param name="Command">The <c>Exec</c> line after any environment, as it is in the file.</param>
/// <param name="Environment">Variables set for the program, in order.</param>
/// <param name="WorkingDirectory">The <c>Path</c> key; empty for none.</param>
/// <param name="Terminal">Whether it runs in a terminal.</param>
public sealed record EntryFields(
    string Name,
    string Command,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    string WorkingDirectory,
    bool Terminal);

/// <summary>
/// Where a launcher's entry is edited, and how.
/// </summary>
/// <remarks>
/// A launcher the user owns -- one dropped on a page from their Desktop, or one already in their
/// own <c>applications</c> directory -- is edited where it is. An installed application is not
/// touched: the edit is saved as the user's own copy under the same desktop-file id, which every
/// launcher then finds first, as the spec intends; the installed file stays, and
/// <see cref="Revert"/> brings it back by removing the copy. While a copy exists, an update to
/// the installed entry is not seen, as on every desktop that does this.
/// </remarks>
public sealed class EntryEditing
{
    private readonly DesktopEntry _entry;
    private readonly CultureInfo _culture;

    private EntryEditing(DesktopEntry entry, string source, string target, string? installed, CultureInfo culture)
    {
        _entry = entry;
        _culture = culture;
        SourcePath = source;
        TargetPath = target;
        InstalledPath = installed;
    }

    /// <summary>The file the fields are read from.</summary>
    public string SourcePath { get; }

    /// <summary>The file a save writes.</summary>
    public string TargetPath { get; }

    /// <summary>The installed entry the user's copy hides or would hide, if there is one.</summary>
    public string? InstalledPath { get; }

    /// <summary>Whether saving writes a copy rather than changing the file the entry came from.</summary>
    public bool MakesCopy => SourcePath != TargetPath;

    /// <summary>Whether there is a user copy that Revert would remove, to bring the installed entry back.</summary>
    public bool CanRevert => !MakesCopy && InstalledPath is not null;

    /// <summary>How <paramref name="icon"/> would be edited, or null for one with no launcher to edit.</summary>
    /// <param name="userDirectory">The user's <c>applications</c> directory.</param>
    /// <param name="installed">The installed file for a desktop-file id, if any; see <see cref="DesktopFiles.Installed"/>.</param>
    public static EntryEditing? For(
        CatalogIcon icon, string userDirectory, Func<string, string?> installed, CultureInfo culture)
    {
        if (icon.Entry is not { FilePath: { } source } entry)
            return null;

        // Dropped on a page: the user's own file, wherever it is.
        if (icon.Item.Kind == ItemKind.File)
            return new EntryEditing(entry, source, source, null, culture);

        return IsIn(userDirectory, source)
            ? new EntryEditing(entry, source, source, installed(entry.Id), culture)
            : new EntryEditing(entry, source, Path.Combine(userDirectory, entry.Id), source, culture);
    }

    /// <summary>The fields as the entry has them now.</summary>
    public EntryFields Read()
    {
        var (environment, command) = ExecEnvironment.Split(_entry.Exec ?? string.Empty);
        return new EntryFields(
            DesktopEntryParser.ResolveLocalized(_entry.Name, _culture),
            command,
            environment,
            _entry.Path ?? string.Empty,
            _entry.Terminal);
    }

    /// <summary>Writes <paramref name="fields"/> to <see cref="TargetPath"/>, changing only what changed.</summary>
    /// <exception cref="IOException">The file could not be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public void Save(EntryFields fields)
    {
        var before = Read();
        var editor = new DesktopEntryEditor(File.ReadAllLines(SourcePath));

        if (fields.Name != before.Name)
            editor.SetString("Name", fields.Name, DesktopEntryParser.ResolvedLocale(_entry.Name, _culture));

        if (fields.Command.Trim() != before.Command || !fields.Environment.SequenceEqual(before.Environment))
        {
            editor.SetRaw("Exec", ExecEnvironment.Join(fields.Environment, fields.Command));
            // A D-Bus activatable application is started over the bus by launchers that know
            // how, and they never read Exec -- so the edit would quietly do nothing there.
            editor.SetRaw("DBusActivatable", null);
        }

        if (fields.WorkingDirectory.Trim() != before.WorkingDirectory)
            editor.SetString("Path", fields.WorkingDirectory.Trim() is { Length: > 0 } path ? path : null);

        if (fields.Terminal != before.Terminal)
            editor.SetRaw("Terminal", fields.Terminal ? "true" : "false");

        Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
        var temporary = TargetPath + ".tmp";
        File.WriteAllLines(temporary, editor.Lines);
        // A user's own launcher on the Desktop needs its execute bit to run there; a copy made
        // from an installed entry is as runnable as the file it came from.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, File.GetUnixFileMode(SourcePath));
        File.Move(temporary, TargetPath, overwrite: true);
    }

    /// <summary>Removes the user's copy, so the installed entry is the one used again.</summary>
    public void Revert()
    {
        if (CanRevert)
            File.Delete(TargetPath);
    }

    private static bool IsIn(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
