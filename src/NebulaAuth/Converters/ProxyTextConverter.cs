using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AchiesUtilities.Web.Proxy;
using Avalonia.Data.Converters;
using NebulaAuth.Model.Entities;

namespace NebulaAuth.Converters;

public class ProxyTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MaProxy p)
        {
            return string.Empty;
        }

        return $"{p.Id}: {p.Data.Address}:{p.Data.Port}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     <c>KeyValuePair&lt;int, ProxyData&gt;</c> (an entry of <c>ProxyStorage.Proxies</c>) -> "Id: Address", for the
///     Linker / MafileMover proxy ComboBox. A generic KeyValuePair cannot be a compiled-binding <c>x:DataType</c>.
/// </summary>
public class ProxyPairTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is KeyValuePair<int, ProxyData> p ? $"{p.Key}: {p.Value.Address}" : string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     MultiBinding: [0] ProxyData, [1] bool display protocol, [2] bool display credentials.
/// </summary>
public class ProxyDataTextMultiConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var value = values.Count > 0 ? values[0] as ProxyData : null;
        var displayProtocol = values.Count > 1 && values[1] is true;
        var displayCredentials = values.Count > 2 && values[2] is true;
        if (value == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (displayProtocol)
        {
            sb.Append(value.Protocol.ToString().ToLowerInvariant());
            sb.Append("://");
        }

        sb.Append(value.Address);
        sb.Append(':');
        sb.Append(value.Port);
        if (displayCredentials && value.AuthEnabled)
        {
            sb.Append(':');
            sb.Append(value.Username);
            sb.Append(':');
            sb.Append(value.Password);
        }

        return sb.ToString();
    }
}
