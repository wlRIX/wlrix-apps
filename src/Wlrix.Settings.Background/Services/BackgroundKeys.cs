namespace Wlrix.Settings.Background.Services;

/// <summary>A value as it was when the window opened, and whether it came from no file at all.</summary>
/// <param name="Value">The effective value, as the daemon reports it.</param>
/// <param name="IsDefault">Set in neither the user's file nor the system one.</param>
public sealed record OpenValue(string Value, bool IsDefault);

/// <summary>
/// The six keys this panel writes, and the decisions about them that are worth testing without a
/// bus: whether the lock screen is following the desktop, and what Cancel has to write back.
/// </summary>
internal static class BackgroundKeys
{
    public const string Image = "background.image";
    public const string Mode = "background.mode";
    public const string Color = "background.color";

    /// <summary>The desktop's keys, in the order the panel reads them.</summary>
    public static readonly IReadOnlyList<string> Desktop = [Image, Mode, Color];

    /// <summary><c>wlrix-lock</c>'s <c>[background]</c> keys, in the same order.</summary>
    public static readonly IReadOnlyList<string> Lock = [.. Desktop.Select(ToLock)];

    /// <summary>
    /// The mode the "No Picture" tile stands for, and which the Fit list therefore leaves out.
    /// </summary>
    public const string SolidMode = "solid";

    /// <summary>
    /// What a picture is fitted with when it is chosen while the mode is <see cref="SolidMode"/>,
    /// which would otherwise leave the picture chosen and not shown. <c>wlrix-bg</c>'s own default.
    /// </summary>
    public const string PictureMode = "fill";

    /// <summary>The lock screen's key for one of the desktop's: <c>lock.background.image</c>.</summary>
    public static string ToLock(string key) => "lock." + key;

    /// <summary>
    /// Whether the lock screen shows what the desktop shows, which is what the "lock screen too"
    /// box starts as. Compared as the effective values, so two files that both leave a key unset
    /// agree.
    /// </summary>
    public static bool LockFollows(IReadOnlyDictionary<string, string> values) =>
        Desktop.All(key =>
            values.TryGetValue(key, out var desktop)
            && values.TryGetValue(ToLock(key), out var locked)
            && string.Equals(desktop, locked, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What putting back <paramref name="open"/> means for the keys in <paramref name="touched"/>.
    /// </summary>
    /// <remarks>
    /// A key that was set somewhere is written back with its value, even when it came from
    /// <c>/etc/wlrix</c>: the daemon seeds the user's file from the system one on its first
    /// write, so removing the key afterwards would fall through to the built-in default rather
    /// than the system's. A key that was set nowhere is removed again instead, because writing
    /// its default would pin it, and an absent <c>image</c> written back as <c>""</c> is how one
    /// monitor says "no picture", not how a fresh install looks.
    /// </remarks>
    public static (IReadOnlyDictionary<string, object> Sets, IReadOnlyList<string> Resets) RevertPlan(
        IReadOnlyDictionary<string, OpenValue> open, IEnumerable<string> touched)
    {
        var sets = new Dictionary<string, object>(StringComparer.Ordinal);
        var resets = new List<string>();
        foreach (var key in touched.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!open.TryGetValue(key, out var value))
                continue;
            if (value.IsDefault)
                resets.Add(key);
            else
                sets[key] = value.Value;
        }
        return (sets, resets);
    }
}
