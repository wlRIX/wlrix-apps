using Wlrix.Common.Localization;

namespace Wlrix.Desks.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The menus come through <c>{loc:Tr}</c>, which reads the same <see cref="Catalog"/>. What is
/// here is what code has to build: the two menu items whose text depends on what they would do,
/// the name a new desk is given, and the failure messages.
///
/// Desk names and window titles are not here. A desk's name is the user's own, kept by the
/// compositor; a window's title is the application's.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Desks.Localization.Strings", typeof(Strings).Assembly);

    public static string CompositorUnreachable => Catalog.Get("CompositorUnreachable");

    /// <summary>The Overview menu's toggle, which says what it will do rather than what is on.</summary>
    public static string GlobalDeskToggle(bool shown) =>
        Catalog.Get(shown ? "HideGlobalDesk" : "ShowGlobalDesk");

    /// <summary>The same for the window-geometry previews.</summary>
    public static string SnapshotsToggle(bool shown) =>
        Catalog.Get(shown ? "HideSnapshots" : "ShowSnapshots");

    /// <summary>
    /// What a newly created desk is called, before anybody renames it.
    /// </summary>
    /// <remarks>
    /// Localized even though it is written through to the compositor and persisted: this is the
    /// name a person reads on the tile, and "Desk 1" is a label rather than an identifier —
    /// nothing keys off it, and renaming is one click away.
    /// </remarks>
    public static string DefaultDeskName(int number) => Catalog.Format("DefaultDeskName", number);

    /// <summary>The compositor answered a command with an <c>err</c> reply.</summary>
    public static string CommandRejected(string reply) => Catalog.Format("CommandRejected", reply);
}
