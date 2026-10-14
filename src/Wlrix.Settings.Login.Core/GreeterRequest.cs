using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wlrix.Settings.Login.Core;

/// <summary>What the helper is asked to do.</summary>
public enum GreeterVerb
{
    /// <summary>Write the login screen's settings into <c>/etc/wlrix/greeter.toml</c>.</summary>
    Write,

    /// <summary>Remove <c>/etc/wlrix/greeter.toml</c>, so the login screen uses the system defaults.</summary>
    Reset,
}

/// <summary>
/// One validated request to <c>wlrix-login-helper</c>.
///
/// As with the Software Manager's <c>HelperRequest</c>, <see cref="TryParse"/> is the only way in,
/// and it runs on both sides: the panel builds its argv with <see cref="ToArguments"/> and checks
/// it, and the helper -- running as root -- parses its own argv again rather than trusting
/// whoever started it. The request carries values only. Which file is written is the helper's
/// to decide, so there is no way to name a path in one.
///
/// <code>
/// write [--palette ID] [--rules S] [--model S] [--layout S] [--variant S] [--options S]
///       [--repeat-delay N] [--repeat-rate N] [--output SPEC]...
/// reset
/// </code>
///
/// An output SPEC is <c>name=DP-1</c> followed by any of <c>,enabled=true</c>,
/// <c>,mode=1920x1080@60</c>, <c>,position=0:0</c>, <c>,scale=1.5</c>, <c>,transform=90</c>.
/// </summary>
public sealed partial class GreeterRequest
{
    /// <summary>A scheme id. Which ones exist is the compositor's to say; this only keeps it a name.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex PaletteId { get; }

    /// <summary>
    /// What an xkb rules, model, layout, variant or options value may contain: names, the comma
    /// that separates layouts, and the colon, parentheses and plus that options and variants use.
    /// No whitespace, no quotes, nothing that could end a TOML string.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_,:()+-]{1,128}$")]
    private static partial Regex XkbValue { get; }

    /// <summary>A DRM connector name: <c>DP-1</c>, <c>HDMI-A-1</c>, <c>eDP-1</c>, <c>Virtual-1</c>.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,31}$")]
    private static partial Regex ConnectorName { get; }

    /// <summary>The settings daemon's ranges for the same keys (<c>schema/table.rs</c>).</summary>
    public const int MaximumRepeatDelay = 10000;

    public const int MaximumRepeatRate = 255;

    /// <summary>No machine has this many displays; a request naming more is not describing one.</summary>
    public const int MaximumOutputs = 16;

    /// <summary>Larger than any display's resolution or any layout's extent.</summary>
    private const int MaximumPixels = 32768;

    public const double MinimumScale = 0.25;

    public const double MaximumScale = 8.0;

    private GreeterRequest(GreeterVerb verb, GreeterConfig config)
    {
        Verb = verb;
        Config = config;
    }

    public GreeterVerb Verb { get; }

    /// <summary>The settings to write. Empty for <see cref="GreeterVerb.Reset"/>.</summary>
    public GreeterConfig Config { get; }

    /// <summary>A request to remove the login screen's own settings.</summary>
    public static GreeterRequest Reset() => new(GreeterVerb.Reset, new GreeterConfig());

    /// <summary>
    /// A request to write <paramref name="config"/>, or the reason it cannot be one. Goes through
    /// <see cref="TryParse"/>, so the panel refuses exactly what the helper would.
    /// </summary>
    public static bool TryCreate(GreeterConfig config, [NotNullWhen(true)] out GreeterRequest? request,
        [NotNullWhen(false)] out string? error) =>
        TryParse(Write(config), out request, out error);

    /// <summary>The argv this request is spelled as, for handing to the helper.</summary>
    public IReadOnlyList<string> ToArguments() => Verb == GreeterVerb.Reset ? ["reset"] : Write(Config);

    private static List<string> Write(GreeterConfig config)
    {
        var arguments = new List<string> { "write" };
        void Add(string flag, string? value)
        {
            if (value is not null)
                arguments.AddRange([flag, value]);
        }

        Add("--palette", config.Palette);
        Add("--rules", config.Rules);
        Add("--model", config.Model);
        Add("--layout", config.Layout);
        Add("--variant", config.Variant);
        Add("--options", config.Options);
        Add("--repeat-delay", config.RepeatDelay?.ToString(CultureInfo.InvariantCulture));
        Add("--repeat-rate", config.RepeatRate?.ToString(CultureInfo.InvariantCulture));
        foreach (var output in config.Outputs)
            Add("--output", Spec(output));
        return arguments;
    }

    private static string Spec(GreeterOutput output)
    {
        var parts = new List<string> { $"name={output.Name}" };
        if (output.Enabled is { } enabled)
            parts.Add(enabled ? "enabled=true" : "enabled=false");
        if (output.Mode is { } mode)
            parts.Add($"mode={mode}");
        if (output is { X: { } x, Y: { } y })
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"position={x}:{y}"));
        if (output.Scale is { } scale)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"scale={scale:0.0##}"));
        if (output.Transform is { } transform)
            parts.Add($"transform={transform}");
        return string.Join(',', parts);
    }

    /// <summary>Parses <c>write …</c> or <c>reset</c>, or explains why it will not.</summary>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out GreeterRequest? request,
        [NotNullWhen(false)] out string? error)
    {
        request = null;
        error = null;

        switch (args)
        {
            case ["reset"]:
                request = Reset();
                return true;
            case ["reset", ..]:
                error = "reset takes no arguments";
                return false;
            case ["write", ..]:
                break;
            case [var verb, ..]:
                error = $"unknown verb: {verb}";
                return false;
            default:
                error = "no verb";
                return false;
        }

        var config = new GreeterConfig();
        var outputs = new List<GreeterOutput>();
        var seen = new HashSet<string>();
        for (var i = 1; i < args.Count; i += 2)
        {
            var flag = args[i];
            if (i + 1 >= args.Count)
            {
                error = $"{flag} needs a value";
                return false;
            }

            var value = args[i + 1];
            if (flag != "--output" && !seen.Add(flag))
            {
                error = $"{flag} given twice";
                return false;
            }

            switch (flag)
            {
                case "--palette" when PaletteId.IsMatch(value):
                    config = config with { Palette = value };
                    break;
                case "--rules" when XkbValue.IsMatch(value):
                    config = config with { Rules = value };
                    break;
                case "--model" when XkbValue.IsMatch(value):
                    config = config with { Model = value };
                    break;
                case "--layout" when XkbValue.IsMatch(value):
                    config = config with { Layout = value };
                    break;
                case "--variant" when XkbValue.IsMatch(value):
                    config = config with { Variant = value };
                    break;
                case "--options" when XkbValue.IsMatch(value):
                    config = config with { Options = value };
                    break;
                case "--repeat-delay" when Int(value, 0, MaximumRepeatDelay) is { } delay:
                    config = config with { RepeatDelay = delay };
                    break;
                case "--repeat-rate" when Int(value, 0, MaximumRepeatRate) is { } rate:
                    config = config with { RepeatRate = rate };
                    break;
                case "--output":
                    if (outputs.Count == MaximumOutputs)
                    {
                        error = $"more than {MaximumOutputs} outputs";
                        return false;
                    }

                    if (!TryParseOutput(value, out var output, out error))
                        return false;
                    if (outputs.Any(o => o.Name == output.Name))
                    {
                        error = $"output {output.Name} given twice";
                        return false;
                    }

                    outputs.Add(output);
                    break;
                case "--palette" or "--rules" or "--model" or "--layout" or "--variant" or "--options"
                    or "--repeat-delay" or "--repeat-rate":
                    error = $"{flag}: not an acceptable value";
                    return false;
                default:
                    error = $"unknown option: {flag}";
                    return false;
            }
        }

        request = new GreeterRequest(GreeterVerb.Write, config with { Outputs = outputs });
        return true;
    }

    private static bool TryParseOutput(string spec, [NotNullWhen(true)] out GreeterOutput? output,
        [NotNullWhen(false)] out string? error)
    {
        output = null;
        error = null;
        string? name = null;
        bool? enabled = null;
        GreeterMode? mode = null;
        int? x = null, y = null;
        double? scale = null;
        string? transform = null;
        var seen = new HashSet<string>();

        foreach (var part in spec.Split(','))
        {
            if (part.Split('=', 2) is not [var key, var value] || !seen.Add(key))
            {
                error = $"--output: malformed: {spec}";
                return false;
            }

            bool ok;
            switch (key)
            {
                case "name":
                    ok = ConnectorName.IsMatch(value);
                    name = value;
                    break;
                case "enabled":
                    ok = value is "true" or "false";
                    enabled = value == "true";
                    break;
                case "mode":
                    // Spelled exactly as ToString spells it, so there is one way to write a mode.
                    mode = GreeterMode.Parse(value);
                    ok = mode is { Width: > 0 and <= MaximumPixels, Height: > 0 and <= MaximumPixels }
                         && mode.RefreshHz is null or (> 0 and <= 1000)
                         && mode.ToString() == value;
                    break;
                case "position":
                    if (value.Split(':') is [var px, var py])
                    {
                        x = Int(px, -MaximumPixels, MaximumPixels);
                        y = Int(py, -MaximumPixels, MaximumPixels);
                    }

                    ok = x is not null && y is not null;
                    break;
                case "scale":
                    ok = double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s)
                         && s is >= MinimumScale and <= MaximumScale;
                    // To the thousandth, which is as fine as the file spells it, so what is
                    // written reads back as exactly what was asked for.
                    scale = Math.Round(s, 3);
                    break;
                case "transform":
                    ok = GreeterOutput.Transforms.Contains(value);
                    transform = value;
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok)
            {
                error = $"--output: {key}: not an acceptable value";
                return false;
            }
        }

        if (name is null)
        {
            error = "--output: no name";
            return false;
        }

        output = new GreeterOutput(name, enabled, mode, x, y, scale, transform);
        return true;
    }

    private static int? Int(string value, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
        && number >= minimum && number <= maximum
            ? number
            : null;
}
