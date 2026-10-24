namespace Wlrix.Settings.International.Services;

/// <summary>One layout in the keyboard's list, and its variant; an empty variant is the layout's default.</summary>
public sealed record LayoutChoice(string Layout, string Variant);

/// <summary>
/// The keyboard as the panel edits it: an ordered list of layouts, a model, and the key that
/// switches between them -- and how that is spelled in xkb's comma-joined strings.
/// </summary>
/// <param name="OtherOptions">
/// Every xkb option that is not a switch key, e.g. <c>ctrl:nocaps</c>. Kept as found, because this
/// panel offers no control for them and must not drop one somebody set by hand.
/// </param>
public sealed record KeyboardSetting(
    IReadOnlyList<LayoutChoice> Layouts,
    string Model,
    string? SwitchKey,
    IReadOnlyList<string> OtherOptions)
{
    /// <summary>What the keyboard is when nothing says otherwise: US, on a generic 105-key board.</summary>
    public static KeyboardSetting Default { get; } = new([new LayoutChoice("us", "")], "pc105", null, []);

    /// <summary>
    /// Read xkb's four strings. Layouts and variants pair up by position (<c>us,jp</c> with
    /// <c>,kana</c>); a variant list shorter than the layout list leaves the rest at their defaults.
    /// </summary>
    public static KeyboardSetting Parse(string? layout, string? variant, string? model, string? options)
    {
        var layouts = Split(layout);
        var variants = Split(variant, keepEmpty: true);
        var choices = layouts
            .Select((name, index) => new LayoutChoice(name, index < variants.Count ? variants[index] : string.Empty))
            .ToList();
        if (choices.Count == 0)
            choices = [.. Default.Layouts];

        var all = Split(options);
        var switchKey = all.FirstOrDefault(IsSwitchKey);
        return new KeyboardSetting(
            choices,
            string.IsNullOrWhiteSpace(model) ? Default.Model : model.Trim(),
            switchKey,
            [.. all.Where(option => !IsSwitchKey(option))]);
    }

    /// <summary>The comma-joined layouts, e.g. <c>us,jp</c>.</summary>
    public string Layout => string.Join(',', Layouts.Select(choice => choice.Layout));

    /// <summary>
    /// The comma-joined variants, e.g. <c>,kana</c>; empty when every layout is on its default, so a
    /// plain setup is not written as a row of commas.
    /// </summary>
    public string Variant => Layouts.All(choice => choice.Variant.Length == 0)
        ? string.Empty
        : string.Join(',', Layouts.Select(choice => choice.Variant));

    /// <summary>
    /// The comma-joined options. The switch key only when there are two layouts to switch between:
    /// with one, it is a key that does nothing and something else could have used.
    /// </summary>
    public string Options => string.Join(',',
        (Layouts.Count > 1 && SwitchKey is { Length: > 0 } key ? [key] : Array.Empty<string>())
        .Concat(OtherOptions));

    /// <summary>
    /// Equal when they would write the same four strings. A record compares its lists by
    /// reference, which would make every freshly read setting differ from the one in force.
    /// </summary>
    public bool Equals(KeyboardSetting? other) =>
        other is not null
        && Layout == other.Layout && Variant == other.Variant && Model == other.Model && Options == other.Options;

    public override int GetHashCode() => HashCode.Combine(Layout, Variant, Model, Options);

    private static bool IsSwitchKey(string option) =>
        option.StartsWith(XkbRegistry.SwitchGroup + ":", StringComparison.Ordinal);

    private static List<string> Split(string? text, bool keepEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var parts = text.Split(',').Select(part => part.Trim());
        return keepEmpty ? [.. parts] : [.. parts.Where(part => part.Length > 0)];
    }
}
