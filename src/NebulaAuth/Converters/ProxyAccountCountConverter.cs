using System;
using System.Globalization;
using Avalonia.Data.Converters;
using NebulaAuth.Model;

namespace NebulaAuth.Converters;

/// <summary>
///     Converts a proxy ID (int) to the number of accounts assigned to it.
///     Returns empty string when count is zero so the badge is invisible.
/// </summary>
public class ProxyAccountCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int proxyId) return string.Empty;
        var count = ProxyAssignmentCache.GetAccountCount(proxyId);
        return count > 0 ? count.ToString() : string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
