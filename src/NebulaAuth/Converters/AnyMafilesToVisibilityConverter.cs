using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NebulaAuth.Converters;

/// <summary>
///     For <c>IsVisible</c>: true when the mafile count is 0 (the "no mafiles" hint is visible).
/// </summary>
public class AnyMafilesToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
