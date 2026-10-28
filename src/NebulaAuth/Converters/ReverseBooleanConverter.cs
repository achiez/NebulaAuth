using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NebulaAuth.Converters;

/// <summary>
///     !value in both directions. For one-way bindings prefer the binding negation <c>{Binding !Flag}</c>;
///     this converter is still needed for TwoWay bindings (e.g. <c>IsChecked</c>).
/// </summary>
public class ReverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }

        throw new ArgumentException("Value must be of type bool", nameof(value));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }

        throw new ArgumentException("Value must be of type bool", nameof(value));
    }
}
