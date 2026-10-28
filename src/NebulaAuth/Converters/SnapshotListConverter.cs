using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace NebulaAuth.Converters;

/// <summary>
///     Any <see cref="IEnumerable" /> -> a snapshot <c>List&lt;object?&gt;</c> for <c>ItemsSource</c>.
///     Avalonia rejects an <c>INotifyCollectionChanged</c> source that is not an <see cref="IList" />
///     (e.g. <c>ObservableDictionary</c> such as <c>ProxyStorage.Proxies</c>: ArgumentException, the list stays empty).
///     Use it only where the source cannot change while the control is shown (e.g. the proxy ComboBox of the Linker and
///     MafileMover wizards: only one dialog at a time, the proxy manager is another dialog).
/// </summary>
public class SnapshotListConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as IEnumerable)?.Cast<object?>().ToList();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
