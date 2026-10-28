using System;
using System.Globalization;
using Avalonia.Data.Converters;
using NebulaAuth.Model;

namespace NebulaAuth.Converters.Background;

/// <summary>
///     For <c>IsVisible</c>: true unless the background mode is <see cref="BackgroundMode.Color" />.
/// </summary>
public class BackgroundImageVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not BackgroundMode.Color;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
