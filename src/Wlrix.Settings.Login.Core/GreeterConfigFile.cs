using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Wlrix.Settings.Login.Core;

/// <summary>
/// Reading and writing <c>/etc/wlrix/greeter.toml</c>.
///
/// The file is a <c>compositor.toml</c>: <c>wlrix-greeter-start</c> hands it to the greeter's
/// compositor with <c>--config</c>. This panel owns three of its sections -- <c>[keyboard]</c>,
/// <c>[appearance]</c> and every <c>[[output]]</c> -- and leaves the rest as it finds it, comments
/// included, because the file is also the administrator's to edit by hand.
/// </summary>
public static partial class GreeterConfigFile
{
    /// <summary>The login screen's own settings.</summary>
    public const string GreeterPath = "/etc/wlrix/greeter.toml";

    /// <summary>
    /// What the login screen uses until <see cref="GreeterPath"/> exists, and what a new one is
    /// seeded from: the compositor takes the first file it finds and does not merge, so a
    /// greeter.toml written from nothing would lose the system's <c>[cursor]</c>.
    /// </summary>
    public const string SystemPath = "/etc/wlrix/compositor.toml";

    /// <summary>The sections this panel writes. Anything else in the file is kept.</summary>
    private static readonly string[] OwnedSections = ["keyboard", "appearance", "output"];

    /// <summary>
    /// A table or array-of-tables header on a line of its own: <c>[name]</c> or <c>[[name]]</c>,
    /// with an optional trailing comment. Dotted names are captured whole.
    /// </summary>
    [GeneratedRegex("""^\s*\[\[?\s*([A-Za-z0-9_.-]+)\s*\]\]?\s*(#.*)?$""")]
    private static partial Regex Header { get; }

    private const string SectionComment =
        "# The login screen's keyboard, color scheme and displays. Written by Login Settings\n" +
        "# (wlrix-settings-login), which replaces these sections and keeps everything else.";

    private const string SeedHeader =
        "# The wlRIX login screen.\n" +
        "#\n" +
        "# Read by the greeter's compositor, which wlrix-greeter-start runs with\n" +
        "# `--config /etc/wlrix/greeter.toml`. The same sections as compositor.toml, and the same\n" +
        "# rules: unknown keys are an error, and `wlrix-compositor --check-config` says whether an\n" +
        "# edit would be accepted. Delete this file and the login screen goes back to\n" +
        "# /etc/wlrix/compositor.toml, which is where it was first copied from.";

    /// <summary>
    /// Reads the panel's settings out of a file's text. Sections and keys this panel does not
    /// own are ignored here; the compositor is the judge of whether they are valid.
    /// </summary>
    public static bool TryParse(string text, out GreeterConfig config, out string? problem)
    {
        config = new GreeterConfig();
        problem = null;

        TomlTable? document;
        try
        {
            document = TomlSerializer.Deserialize<TomlTable>(text);
        }
        catch (TomlException ex)
        {
            problem = ex.Message.Split('\n')[0].Trim();
            return false;
        }

        if (document is null)
            return true;

        var keyboard = document.TryGetValue("keyboard", out var k) ? k as TomlTable : null;
        var appearance = document.TryGetValue("appearance", out var a) ? a as TomlTable : null;
        var outputs = new List<GreeterOutput>();
        if (document.TryGetValue("output", out var o) && o is TomlTableArray entries)
        {
            foreach (var entry in entries)
            {
                if (String(entry, "name") is not { } name)
                    continue;
                int? x = null, y = null;
                if (entry.TryGetValue("position", out var p) && p is TomlArray { Count: 2 } position)
                {
                    x = Integer(position[0]);
                    y = Integer(position[1]);
                }

                outputs.Add(new GreeterOutput(
                    name,
                    entry.TryGetValue("enabled", out var e) && e is bool enabled ? enabled : null,
                    GreeterMode.Parse(String(entry, "mode")),
                    x,
                    y,
                    entry.TryGetValue("scale", out var s) ? Number(s) : null,
                    String(entry, "transform")));
            }
        }

        config = new GreeterConfig
        {
            Palette = appearance is null ? null : String(appearance, "palette"),
            Rules = keyboard is null ? null : String(keyboard, "rules"),
            Model = keyboard is null ? null : String(keyboard, "model"),
            Layout = keyboard is null ? null : String(keyboard, "layout"),
            Variant = keyboard is null ? null : String(keyboard, "variant"),
            Options = keyboard is null ? null : String(keyboard, "options"),
            RepeatDelay = keyboard is not null && keyboard.TryGetValue("repeat_delay", out var d) ? Integer(d) : null,
            RepeatRate = keyboard is not null && keyboard.TryGetValue("repeat_rate", out var r) ? Integer(r) : null,
            Outputs = outputs,
        };
        return true;
    }

    /// <summary>
    /// The text of a greeter.toml holding <paramref name="config"/>.
    ///
    /// <paramref name="existing"/> is the current greeter.toml when there is one. When there is
    /// not, pass the system compositor.toml and <paramref name="seeding"/>: its leading comment,
    /// which describes the system file, is replaced by one describing this one.
    /// </summary>
    public static string Apply(string existing, GreeterConfig config, bool seeding)
    {
        var lines = existing.ReplaceLineEndings("\n").Split('\n').ToList();
        if (seeding)
        {
            lines = DropLeadingComment(lines);
            lines.InsertRange(0, [.. SeedHeader.Split('\n'), ""]);
        }

        var kept = DropOwnedSections(lines);
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
            kept.RemoveAt(kept.Count - 1);

        var text = new StringBuilder();
        foreach (var line in kept)
            text.Append(line).Append('\n');

        var sections = Render(config);
        if (sections.Length > 0)
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append(SectionComment).Append('\n').Append(sections);
        }

        return text.ToString();
    }

    /// <summary>
    /// The lines with the panel's sections taken out: each owned header, everything up to the
    /// next header, and the run of comment lines directly above it (which is that section's own
    /// comment, as <see cref="Render"/> writes it, and would otherwise pile up on every save).
    /// </summary>
    private static List<string> DropOwnedSections(List<string> lines)
    {
        var kept = new List<string>();
        var dropping = false;
        foreach (var line in lines)
        {
            if (Header.Match(line) is { Success: true } header)
            {
                var name = header.Groups[1].Value;
                dropping = OwnedSections.Contains(name) || OwnedSections.Any(s => name.StartsWith(s + ".", StringComparison.Ordinal));
                if (dropping)
                {
                    while (kept.Count > 0 && kept[^1].TrimStart().StartsWith('#'))
                        kept.RemoveAt(kept.Count - 1);
                    continue;
                }
            }

            if (!dropping)
                kept.Add(line);
        }

        return kept;
    }

    /// <summary>The lines after the comment block a file opens with, up to its first blank line.</summary>
    private static List<string> DropLeadingComment(List<string> lines)
    {
        var start = 0;
        while (start < lines.Count && lines[start].TrimStart().StartsWith('#'))
            start++;
        while (start < lines.Count && string.IsNullOrWhiteSpace(lines[start]))
            start++;
        return lines[start..];
    }

    /// <summary>The three sections, in the order a person would read them.</summary>
    private static string Render(GreeterConfig config)
    {
        var text = new StringBuilder();

        if (config.HasKeyboard)
        {
            text.Append("[keyboard]\n");
            Line(text, "rules", config.Rules);
            Line(text, "model", config.Model);
            Line(text, "layout", config.Layout);
            Line(text, "variant", config.Variant);
            Line(text, "options", config.Options);
            Line(text, "repeat_delay", config.RepeatDelay);
            Line(text, "repeat_rate", config.RepeatRate);
        }

        if (config.Palette is not null)
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append("[appearance]\n");
            Line(text, "palette", config.Palette);
        }

        foreach (var output in config.Outputs)
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append("[[output]]\n");
            Line(text, "name", output.Name);
            Line(text, "mode", output.Mode?.ToString());
            if (output is { X: { } x, Y: { } y })
                text.Append(CultureInfo.InvariantCulture, $"position = [{x}, {y}]\n");
            if (output.Scale is { } scale)
                text.Append(CultureInfo.InvariantCulture, $"scale = {FormatScale(scale)}\n");
            Line(text, "transform", output.Transform);
            if (output.Enabled is { } enabled)
                text.Append(CultureInfo.InvariantCulture, $"enabled = {(enabled ? "true" : "false")}\n");
        }

        return text.ToString();
    }

    /// <summary>A float TOML will read back as a float: <c>1.0</c>, never <c>1</c>.</summary>
    private static string FormatScale(double scale) => scale.ToString("0.0##", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder text, string key, string? value)
    {
        if (value is not null)
            text.Append(key).Append(" = ").Append(Quote(value)).Append('\n');
    }

    private static void Line(StringBuilder text, string key, int? value)
    {
        if (value is { } number)
            text.Append(CultureInfo.InvariantCulture, $"{key} = {number}\n");
    }

    /// <summary>A TOML basic string. The values are validated long before this, but quoted properly regardless.</summary>
    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case < ' ': text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}"); break;
                default: text.Append(c); break;
            }
        }

        return text.Append('"').ToString();
    }

    private static string? String(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) ? value as string : null;

    private static int? Integer(object? value) => value switch
    {
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        int number => number,
        _ => null,
    };

    private static double? Number(object? value) => value switch
    {
        double number => number,
        long number => number,
        _ => null,
    };
}
