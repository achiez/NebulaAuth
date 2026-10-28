using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using AchiesUtilities.Collections;
using AchiesUtilities.Web.Proxy;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Avalonia.Utilities;

namespace NebulaAuth.View;

/// <summary>
///     Row of the proxy list in <see cref="ProxyManagerView" />: one entry of <c>ProxyStorage.Proxies</c>.
///     Compiled bindings cannot declare the generic <c>KeyValuePair&lt;int, ProxyData&gt;</c> as <c>x:DataType</c>, so the
///     list shows these rows and hands <see cref="Pair" /> to the VM (SelectedProxy, command parameters).
///     Value equality: the ListBox finds the row of a VM-selected pair.
/// </summary>
public sealed record ProxyManagerViewItem(int Key, ProxyData Value)
{
    public KeyValuePair<int, ProxyData> Pair => KeyValuePair.Create(Key, Value);
}

public partial class ProxyManagerView
{
    /// <summary>
    ///     <c>ProxyManagerVM.SelectedProxy</c> (<c>KeyValuePair&lt;int, ProxyData&gt;?</c>) &lt;-&gt; selected
    ///     <see cref="ProxyManagerViewItem" />.
    /// </summary>
    public static IValueConverter SelectedProxyConverter { get; } = new SelectedProxyItemConverter();

    /// <summary>
    ///     MultiBinding [<see cref="ProxyManagerViewItem" /> row, <c>ProxyManagerVM.DefaultProxy</c>] -&gt; true when the row is
    ///     the default proxy (marks its heart button).
    /// </summary>
    public static IMultiValueConverter IsDefaultProxyConverter { get; } = new FuncMultiValueConverter<object?, bool>(
        (IReadOnlyList<object?> values) => values.Count > 1 && values[0] is ProxyManagerViewItem row &&
                                           values[1] is KeyValuePair<int, ProxyData> def && row.Pair.Equals(def));

    private sealed class SelectedProxyItemConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is KeyValuePair<int, ProxyData> kvp ? new ProxyManagerViewItem(kvp.Key, kvp.Value) : null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is ProxyManagerViewItem item ? item.Pair : null;
        }
    }

    /// <summary>
    ///     Live list of the proxy dictionary for the ListBox. Avalonia items controls need an <c>IList</c> when the source
    ///     raises CollectionChanged, which <see cref="ObservableDictionary{TKey,TValue}" /> is not. The subscription is
    ///     weak, so the static <c>ProxyStorage.Proxies</c> does not keep a closed dialog alive.
    /// </summary>
    private sealed class ProxyItems : ObservableCollection<ProxyManagerViewItem>,
        IWeakEventSubscriber<NotifyCollectionChangedEventArgs>
    {
        private readonly ObservableDictionary<int, ProxyData> _source;

        public ProxyItems(ObservableDictionary<int, ProxyData> source)
        {
            _source = source;
            foreach (var (key, value) in source)
                Items.Add(new ProxyManagerViewItem(key, value));
            WeakEvents.CollectionChanged.Subscribe(source, this);
        }

        public void OnEvent(object? sender, WeakEvent ev, NotifyCollectionChangedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(Reload);
                return;
            }

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems != null:
                    var index = e.NewStartingIndex;
                    foreach (KeyValuePair<int, ProxyData> kvp in e.NewItems)
                    {
                        var item = new ProxyManagerViewItem(kvp.Key, kvp.Value);
                        if (index >= 0 && index <= Count)
                            Insert(index++, item);
                        else
                            Add(item);
                    }

                    break;
                case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                    foreach (KeyValuePair<int, ProxyData> kvp in e.OldItems)
                    {
                        var i = IndexOfKey(kvp.Key);
                        if (i >= 0) RemoveAt(i);
                    }

                    break;
                case NotifyCollectionChangedAction.Replace when e.NewItems != null:
                    foreach (KeyValuePair<int, ProxyData> kvp in e.NewItems)
                    {
                        var item = new ProxyManagerViewItem(kvp.Key, kvp.Value);
                        var i = IndexOfKey(kvp.Key);
                        if (i >= 0)
                            this[i] = item;
                        else
                            Add(item);
                    }

                    break;
                default:
                    Reload();
                    break;
            }
        }

        /// <summary>
        ///     Rebuilds the list from the dictionary with a single Reset (all rows are re-created).
        /// </summary>
        public void Reload()
        {
            Items.Clear();
            foreach (var (key, value) in _source)
                Items.Add(new ProxyManagerViewItem(key, value));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private int IndexOfKey(int key)
        {
            for (var i = 0; i < Count; i++)
            {
                if (this[i].Key == key) return i;
            }

            return -1;
        }
    }
}
