namespace Wlrix.Desks.Models;

/// <summary>
/// How large the desk snapshots are drawn: a percentage of the screen layout's own size.
/// </summary>
/// <remarks>
/// IRIX's overview offered the presets in its Desk menu's Scaling submenu, with 8% the default,
/// and a Custom dialog whose slider ran from 4% to 18% for anything else. The numbers are the
/// overview's, measured off it rather than chosen here.
/// </remarks>
public static class DeskScale
{
    public const int Default = 8;

    public const int Minimum = 4;

    public const int Maximum = 18;

    /// <summary>The Scaling submenu's fixed choices, in the order it lists them.</summary>
    public static IReadOnlyList<int> Presets { get; } = [6, 7, 8, 9, 10];

    public static bool IsPreset(int percent) => Presets.Contains(percent);

    public static bool IsInRange(int percent) => percent is >= Minimum and <= Maximum;
}
