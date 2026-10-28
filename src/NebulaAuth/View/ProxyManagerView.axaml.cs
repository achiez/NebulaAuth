using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NebulaAuth.Utility;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View;

public partial class ProxyManagerView : UserControl
{
    private ProxyItems? _proxyItems;

    public ProxyManagerView()
    {
        InitializeComponent();
        // Tunnel: the multi-line TextBox would insert a line break for Ctrl+Enter before a bubbling handler runs
        AddProxyBox.AddHandler(KeyDownEvent, ProxyInput_KeyDown, RoutingStrategies.Tunnel);
        KeyBindingHelper.AddMacBackspaceForDelete(ProxyList);
    }

    /// <summary>
    ///     Platform command modifier (Cmd on macOS) for the "add" shortcut; Ctrl keeps working everywhere.
    /// </summary>
    private KeyModifiers CommandModifiers =>
        (this.GetPlatformSettings() ?? Application.Current?.PlatformSettings)?.HotkeyConfiguration.CommandModifiers
        ?? KeyModifiers.Control;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var commandModifiers = CommandModifiers;
        if (commandModifiers != KeyModifiers.Control)
            ToolTip.SetTip(AddProxyBtn, new KeyGesture(Key.Enter, commandModifiers).ToString("p", null));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _proxyItems = DataContext is ProxyManagerVM vm ? new ProxyItems(vm.Proxies) : null;
        ProxyList.ItemsSource = _proxyItems;
    }

    private void ProxyInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(CommandModifiers))) return;
        e.Handled = true;
        AddProxyBtn.Command?.Execute(null);
    }

    private void Tabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged of the proxy ListBox bubbles up here as well
        if (!ReferenceEquals(e.Source, Tabs) || DataContext is not ProxyManagerVM vm) return;

        if (Tabs.SelectedIndex == 0)
        {
            // Account counters of the rows are evaluated per row: rebuild the rows after a bulk assignment
            var selected = ProxyList.SelectedItem;
            _proxyItems?.Reload();
            ProxyList.SelectedItem = selected;
        }
        else
        {
            vm.RefreshAssignmentCounters();
        }
    }
}
