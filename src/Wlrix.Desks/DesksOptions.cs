using CommandLine;

namespace Wlrix.Desks;

/// <summary>
/// The command line, as the IRIX <c>ov</c> man page describes it.
/// </summary>
/// <remarks>
/// Mutable auto-properties with the implicit parameterless constructor, because
/// CommandLineParser binds options by reflection; its immutable-type path wants a constructor
/// it can fill positionally, which is more ceremony than five booleans deserve.
///
/// <para>
/// The long names are <c>ov</c>'s own spellings and are left exactly as the man page prints
/// them — which is why <c>hideSnapShots</c> and <see cref="HideSnapshots"/> disagree about that
/// capital S. The short forms (<c>--nown</c>, <c>--hss</c>, <c>--ngd</c>) are not options here;
/// they are expanded before the parser sees them, see
/// <see cref="DesksCommandLine.ExpandAliases"/>.
/// </para>
/// </remarks>
internal sealed class DesksOptions
{
    [Option("noWindowName", HelpText =
        "Turn off displaying of window names when the mouse passes over the miniature windows. "
        + "Application IDs appear instead.")]
    public bool NoWindowName { get; set; }

    [Option("hideSnapShots", HelpText =
        "Hide the desk snapshots (the miniature desk representations). Desk names appear as "
        + "small space-saving buttons instead.")]
    public bool HideSnapshots { get; set; }

    [Option("noGlobalDesk", HelpText = "Hide the global desk.")]
    public bool NoGlobalDesk { get; set; }

    [Option("new", HelpText =
        "Create a new overview, disregarding the \"runonce\" feature.")]
    public bool New { get; set; }

    // Undocumented on purpose, which is what Hidden means: it keeps working, and `--help` does
    // not advertise a developer switch as though it were a feature.
    [Option("demo", Hidden = true, HelpText = "Run the fabricated sample layout, with no compositor.")]
    public bool Demo { get; set; }
}
