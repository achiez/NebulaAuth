using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NebulaAuth.Converters;

public class BoolToMafileNamingConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue)
            return null;
        return boolValue ? "Login" : "Steam ID";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
