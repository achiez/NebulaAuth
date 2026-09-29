using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NebulaAuth.Converters;

/// <summary>
///     Bridges a <c>Color?</c> source (Settings.IconColor / SettingsVM.IconColor) and the
///     non-nullable <c>ColorPicker.Color</c> / <c>ColorView.Color</c>. null -> the ConverterParameter color
///     (a Color or a "#RRGGBB" string; default Red). Back: the picked color.
/// </summary>
public class NullableColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color) return color;
        if (parameter is Color fallback) return fallback;
        if (parameter is string s && Color.TryParse(s, out var parsed)) return parsed;
        return Colors.Red;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Color color ? color : BindingOperations.DoNothing;
    }
}
