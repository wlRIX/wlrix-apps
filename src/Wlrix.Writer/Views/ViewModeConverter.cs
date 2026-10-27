// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Data.Converters;
using Wlrix.Writer.Models;

namespace Wlrix.Writer.Views;

/// <summary>Whether a <see cref="ViewMode"/> is the one named by the parameter: a radio item's mark.</summary>
public sealed class ViewModeConverter : IValueConverter
{
    public static ViewModeConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ViewMode mode && parameter is ViewMode wanted && mode == wanted;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
