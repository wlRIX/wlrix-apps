using System.Globalization;
using Tmds.DBus.Protocol;

namespace Wlrix.Settings.Client;

/// <summary>
/// Between D-Bus variants and ordinary CLR values.
///
/// The daemon is liberal about what it accepts — it takes any integer width and unwraps a
/// doubly-wrapped variant — so this side does not have to be clever. It only has to send
/// something unambiguous, and to read back the six shapes a wlRIX setting can have.
/// </summary>
internal static class Values
{
    /// <summary>
    /// A variant as the nearest CLR value: <c>bool</c>, <c>long</c>, <c>double</c>,
    /// <c>string</c> or <c>string[]</c>.
    ///
    /// Anything else answers null rather than a guess. A setting the daemon describes is always
    /// one of those, so a null here means the daemon grew a shape this client does not know —
    /// which should leave the control blank, not throw.
    /// </summary>
    internal static object? ToClr(VariantValue value) => value.Type switch
    {
        VariantValueType.Bool => value.GetBool(),
        VariantValueType.Byte => (long)value.GetByte(),
        VariantValueType.Int16 => (long)value.GetInt16(),
        VariantValueType.UInt16 => (long)value.GetUInt16(),
        VariantValueType.Int32 => (long)value.GetInt32(),
        VariantValueType.UInt32 => (long)value.GetUInt32(),
        VariantValueType.Int64 => value.GetInt64(),
        VariantValueType.UInt64 => (long)value.GetUInt64(),
        VariantValueType.Double => value.GetDouble(),
        VariantValueType.String => value.GetString(),
        VariantValueType.Array => value.GetArray<string>(),
        // A variant inside the variant, which some senders produce for `a{sv}`.
        VariantValueType.Variant => ToClr(value.GetVariantValue()),
        _ => null,
    };

    /// <summary>
    /// A CLR value as a variant to send.
    ///
    /// Integers go out as <c>x</c> (int64) and fractions as <c>d</c>, which is what the schema
    /// declares. The one worth knowing: an <c>int</c> destined for a fractional setting is
    /// converted by the daemon, not refused — TOML is stricter than a person is, and
    /// <c>deadzone = 0</c> is a parse error for a field the owner declared as a float.
    /// </summary>
    internal static VariantValue ToVariant(string key, object value) => value switch
    {
        bool flag => VariantValue.Bool(flag),
        int number => VariantValue.Int64(number),
        long number => VariantValue.Int64(number),
        double number => VariantValue.Double(number),
        float number => VariantValue.Double(number),
        string text => VariantValue.String(text),
        IEnumerable<string> items => Array(items),
        _ => throw new ArgumentException(
            $"{key}: {value.GetType().Name} is not a shape wlrix-settings-daemon carries", nameof(value)),
    };

    private static VariantValue Array(IEnumerable<string> items) =>
        VariantValue.Array(items.ToArray());

    /// <summary>
    /// A value as a person would read it, for a log line or a tooltip.
    ///
    /// Invariant culture on purpose: this is diagnostic text about a config file, and a comma
    /// where the file has a decimal point is a confusing way to describe it.
    /// </summary>
    public static string Show(object? value) => value switch
    {
        null => "unset",
        bool flag => flag ? "true" : "false",
        string[] items => string.Join(", ", items),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
