using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NebulaAuth.Converters;

/// <summary>
///     value * parameter (parameter parsed with the invariant culture, e.g. ConverterParameter=0.6).
/// </summary>
public class CoefficientConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Convert.ToDouble(value) * System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var first = value as double? ?? 0;
        var second = parameter as double? ?? 0;
        if (second == 0) second = 1;
        return first / second;
    }
}
