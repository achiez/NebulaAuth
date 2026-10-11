using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NebulaAuth.View.NebulaLink;

public partial class ConnectPage : UserControl
{
    public ConnectPage()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Dispatcher.UIThread.Post(() => LinkBox.Focus(), DispatcherPriority.Input);
    }
}
