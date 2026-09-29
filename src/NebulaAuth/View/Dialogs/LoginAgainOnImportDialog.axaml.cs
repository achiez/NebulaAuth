using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NebulaAuth.Utility;

namespace NebulaAuth.View.Dialogs;

public partial class LoginAgainOnImportDialog : UserControl
{
    public LoginAgainOnImportDialog()
    {
        InitializeComponent();
        KeyBindingHelper.AddMacBackspaceForDelete(ProxyBox);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Focus after the open animation has attached the popup host. Select any pre-filled text so typing
        // replaces it instead of being inserted in front of it.
        Dispatcher.UIThread.Post(() =>
        {
            PasswordField.Focus();
            PasswordField.SelectAll();
        }, DispatcherPriority.Input);
    }
}
