// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Wlrix.Common.Desktop;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Platform;

namespace Wlrix.Settings.Utilities.Services;

/// <summary>
/// What the system says opens what, and the writes that change it.
/// </summary>
/// <remarks>
/// Everything read here is read the same way Files reads it, through
/// <see cref="ApplicationHandlers"/>, so what this panel shows as the default is what a
/// double-click in Files runs.
/// </remarks>
internal sealed class Associations
{
    private readonly ApplicationHandlers _handlers;
    private readonly IReadOnlyList<string> _mimeDirectories;

    public Associations(
        IReadOnlyList<DesktopEntry> entries,
        ApplicationHandlers handlers,
        IReadOnlyList<string> mimeDirectories,
        UserMimeTypes userTypes,
        TerminalList terminals)
    {
        _handlers = handlers;
        _mimeDirectories = mimeDirectories;
        UserTypes = userTypes;
        Terminals = terminals;
        Applications = [.. entries
            .Where(static entry => !entry.NoDisplay && !entry.Hidden && !string.IsNullOrWhiteSpace(entry.Exec))
            .GroupBy(static entry => entry.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase)];
        ReloadTypes();
    }

    /// <summary>The session's: every installed entry, the whole chain, the XDG mime directories.</summary>
    public static Associations ForSession()
    {
        var entries = new DesktopEntryScanner().Scan();
        return new Associations(
            entries,
            new ApplicationHandlers(entries),
            [.. SharedMimeDatabase.DefaultSearchPaths()],
            UserMimeTypes.ForSession(),
            TerminalList.ForSession());
    }

    /// <summary>Every application worth offering, by name.</summary>
    public IReadOnlyList<DesktopEntry> Applications { get; }

    /// <summary>The database as it is now, the user's packages included.</summary>
    public SharedMimeDatabase Database { get; private set; } = null!;

    /// <summary>The database without the user's packages: what Revert to System goes back to.</summary>
    public SharedMimeDatabase SystemDatabase { get; private set; } = null!;

    public UserMimeTypes UserTypes { get; }

    public TerminalList Terminals { get; }

    /// <summary>An entry's name in the session's language.</summary>
    public static string NameOf(DesktopEntry entry) =>
        DesktopEntryParser.ResolveLocalized(entry.Name, CultureInfo.CurrentUICulture) is { Length: > 0 } name
            ? name
            : entry.Id;

    /// <summary>An installed entry by id, or null.</summary>
    public DesktopEntry? Find(string id) => _handlers.Find(id);

    /// <summary>Rereads the type database, after Apply has recompiled it.</summary>
    public void ReloadTypes()
    {
        Database = SharedMimeDatabase.Load(_mimeDirectories);
        var user = UserTypes.MimeDirectory;
        SystemDatabase = SharedMimeDatabase.Load(
            _mimeDirectories.Where(dir => !string.Equals(Path.TrimEndingDirectorySeparator(dir), user, StringComparison.Ordinal)));
    }

    /// <summary>Whether a type comes from the system, so it can be reverted and not removed.</summary>
    public bool IsSystemType(string mimeType) => SystemDatabase.AllTypes().Contains(mimeType);

    /// <summary>A type's applications in the order they are tried, its own and no inherited ones.</summary>
    public IReadOnlyList<string> OrderFor(string mimeType) =>
        [.. _handlers.For(mimeType, inherited: false).Select(static entry => entry.Id)];

    /// <summary>What a role's menu shows as chosen now, or null when nothing is.</summary>
    public string? CurrentFor(UtilityRole role) => role.Kind switch
    {
        RoleKind.Terminal => Terminals.Preferred() is { } id && Find(id) is not null
            ? id
            : Applications.FirstOrDefault(role.Offers)?.Id,
        _ => OrderFor(role.PrimaryType).FirstOrDefault(),
    };

    /// <summary>What a role's menu offers without Other….</summary>
    public IReadOnlyList<DesktopEntry> OfferedFor(UtilityRole role) => [.. Applications.Where(role.Offers)];

    /// <summary>Writes every pending change.</summary>
    /// <returns>Null when everything was written, otherwise what went wrong.</returns>
    public async Task<string?> ApplyAsync(PendingChanges changes)
    {
        try
        {
            if (changes.Orders.Count > 0)
            {
                // Baselines first, from the files as they are, before the user's is touched.
                var list = MimeAppsList.Read(_handlers.MimeAppsPath);
                foreach (var (type, order) in changes.Orders)
                    PendingChanges.Write(list, type, order, _handlers.Associated(type));
                list.Save(_handlers.MimeAppsPath);
            }

            if (changes.Terminal is { } terminal)
                Terminals.SetPreferred(terminal);

            if (changes.TouchesTypes)
            {
                foreach (var type in changes.TypeDeletions)
                    UserTypes.Delete(type);
                foreach (var type in changes.TypeEdits.Values)
                    UserTypes.Write(type);

                var error = await UserTypes.UpdateDatabaseAsync().ConfigureAwait(false);
                ReloadTypes();
                if (error is not null)
                    return error;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        return null;
    }
}
