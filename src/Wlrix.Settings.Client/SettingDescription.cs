using Tmds.DBus.Protocol;

namespace Wlrix.Settings.Client;

/// <summary>
/// What a setting is, as the daemon describes it: enough to render a control, validate what
/// somebody types into it, and say what will happen when they do.
///
/// The point of it existing at all is that a panel should not carry a second copy of wlRIX's
/// defaults and ranges. <c>Wlrix.Settings.Keyboard</c> used to hard-code <c>"us"</c>,
/// <c>"pc105"</c>, <c>200</c> and <c>25</c>; those now come from here, so there is one place
/// they are written down.
/// </summary>
public sealed record SettingDescription
{
    /// <summary>The full key, <c>&lt;namespace&gt;.&lt;toml path&gt;</c>.</summary>
    public required string Key { get; init; }

    public required string Namespace { get; init; }

    /// <summary>
    /// <c>bool</c>, <c>int</c>, <c>double</c>, <c>string</c>, <c>enum</c> or
    /// <c>string-list</c> — what to switch on to pick a control.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>The D-Bus signature values of this setting are carried as.</summary>
    public required string Signature { get; init; }

    /// <summary>One line, for a label or a tooltip.</summary>
    public required string Summary { get; init; }

    /// <summary>The longer version, for help text.</summary>
    public required string Description { get; init; }

    /// <summary><c>ms</c>, <c>s</c>, <c>hz</c>, <c>px</c>, or empty — a suffix for a spinner.</summary>
    public required string Unit { get; init; }

    /// <summary>Which program reads this. Empty when nothing running does.</summary>
    public required string Owner { get; init; }

    /// <summary>
    /// What it costs to apply: <c>live</c>, <c>restart</c>, <c>next-login</c> or <c>none</c>.
    /// </summary>
    public required string Reload { get; init; }

    /// <summary>Where a write to this setting lands.</summary>
    public required string File { get; init; }

    /// <summary>
    /// Whether <see cref="Default"/> means anything.
    ///
    /// Load-bearing, and easy to skip: <c>compositor.keyboard.layout</c> genuinely has no
    /// default — absent means "let libxkbcommon decide" — while <c>repeat_delay</c> defaults to
    /// 200. A panel has to be able to show "system default" and "200" as different things, and
    /// a <see cref="Default"/> of <c>""</c> cannot carry that on its own.
    /// </summary>
    public required bool HasDefault { get; init; }

    /// <summary>The declared default, when <see cref="HasDefault"/>.</summary>
    public object? Default { get; init; }

    /// <summary>The inclusive range, for a number.</summary>
    public double? Min { get; init; }

    /// <summary>The inclusive range, for a number.</summary>
    public double? Max { get; init; }

    /// <summary>The permitted values, for an enum. Empty otherwise.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// English labels for <see cref="Choices"/>, in the same order.
    ///
    /// Untranslated, and deliberately so: presentation belongs to the app, which has a
    /// localization story the daemon does not. They are here so an app with no translation for
    /// a wlRIX enum can still render a dropdown rather than hardcoding wlRIX's values.
    /// </summary>
    public IReadOnlyList<string> ChoiceLabels { get; init; } = [];

    /// <summary>
    /// Read a description off the wire.
    ///
    /// Every field is looked up rather than positional, because the daemon sends
    /// <c>a{sv}</c> exactly so it can gain entries without breaking clients — a missing one
    /// must mean "older daemon", not an exception.
    /// </summary>
    internal static SettingDescription From(Dictionary<string, VariantValue> described)
    {
        string Text(string name) =>
            described.TryGetValue(name, out var value) ? Values.ToClr(value) as string ?? string.Empty : string.Empty;

        double? Number(string name) =>
            described.TryGetValue(name, out var value) && Values.ToClr(value) is { } clr
                ? Convert.ToDouble(clr, System.Globalization.CultureInfo.InvariantCulture)
                : null;

        IReadOnlyList<string> Strings(string name) =>
            described.TryGetValue(name, out var value) && Values.ToClr(value) is string[] items ? items : [];

        var hasDefault = described.TryGetValue("has_default", out var flag)
            && Values.ToClr(flag) is bool set
            && set;

        return new SettingDescription
        {
            Key = Text("key"),
            Namespace = Text("namespace"),
            Kind = Text("kind"),
            Signature = Text("signature"),
            Summary = Text("summary"),
            Description = Text("description"),
            Unit = Text("unit"),
            Owner = Text("owner"),
            Reload = Text("reload"),
            File = Text("file"),
            HasDefault = hasDefault,
            Default = hasDefault && described.TryGetValue("default", out var value) ? Values.ToClr(value) : null,
            Min = Number("min"),
            Max = Number("max"),
            Choices = Strings("choices"),
            ChoiceLabels = Strings("choice_labels"),
        };
    }
}

/// <summary>
/// What became of a write, per owning program.
///
/// A write always reaches the file; this says whether anything picked it up. That is the
/// difference between a panel saying "the compositor isn't running, this applies at next
/// login" and one that appears to have done nothing.
/// </summary>
public sealed class ApplyResult(IReadOnlyDictionary<string, string> outcomes)
{
    /// <summary>Program name to outcome.</summary>
    public IReadOnlyDictionary<string, string> Outcomes { get; } = outcomes;

    /// <summary>Whether every owner picked the change up immediately.</summary>
    public bool AppliedEverywhere =>
        Outcomes.Count > 0 && Outcomes.Values.All(outcome => outcome == "applied");

    /// <summary>A line a panel can put in a status bar, or null when everything applied.</summary>
    public string? Advice => Outcomes
        .Where(entry => entry.Value != "applied")
        .Select(entry => entry.Value switch
        {
            "not-running" => $"Saved. {entry.Key} is not running; it will read this at the next start.",
            "restart-required" => $"Saved. Restart {entry.Key} to pick this up.",
            "next-login" => "Saved. This takes effect at the next login.",
            _ => $"Saved. {entry.Key}: {entry.Value}.",
        })
        .FirstOrDefault();
}

/// <summary>Settings whose effective value changed, and who caused it.</summary>
public sealed class SettingsChangedEventArgs(IReadOnlyDictionary<string, object?> values, string origin)
    : EventArgs
{
    /// <summary>Only the keys that actually moved, with their new effective values.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; } = values;

    /// <summary>
    /// The unique bus name of whoever called Set, or <c>external</c> for a hand-edited file.
    ///
    /// Compare against <see cref="SettingsClient.UniqueName"/> and ignore the match, or a panel
    /// will act on the echo of its own write.
    /// </summary>
    public string Origin { get; } = origin;
}

/// <summary>A config file broke, or stopped being broken.</summary>
public sealed class SettingsFileInvalidEventArgs(string ns, string path, string message) : EventArgs
{
    public string Namespace { get; } = ns;

    public string Path { get; } = path;

    /// <summary>The parser's own message. Empty when the file has just recovered.</summary>
    public string Message { get; } = message;
}
