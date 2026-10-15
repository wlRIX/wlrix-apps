// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Wlrix.Settings.Mouse.Converters;

/// <summary>
/// Lights one diamond of a row: true when the bound preset index is this diamond's
/// <c>ConverterParameter</c>, and checking a diamond sets the index to its parameter.
/// Unchecking sets nothing -- a radio button unchecks because another one was checked, and that
/// one has already said what the index is.
/// </summary>
public sealed class PresetConverter : IValueConverter
{
    public static PresetConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && Parameter(parameter) == index;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && Parameter(parameter) is { } index ? index : BindingOperations.DoNothing;

    private static int? Parameter(object? parameter) => parameter switch
    {
        int index => index,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) => index,
        _ => null,
    };
}
