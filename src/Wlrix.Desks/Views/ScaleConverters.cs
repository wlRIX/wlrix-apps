using System.Globalization;
using Avalonia.Data.Converters;

namespace Wlrix.Desks.Views;

/// <summary>Converters for the Scaling submenu.</summary>
public static class ScaleConverters
{
    /// <summary>
    /// Whether a preset's radio item is checked: the bound scale against the item's own
    /// percentage, which XAML hands over as text.
    /// </summary>
    public static readonly IValueConverter IsPreset = new FuncValueConverter<int?, object?, bool>(
        (scale, parameter) => scale is { } percent
                              && int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var preset)
                              && preset == percent);
}
