using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NebulaAuth.View.Dialogs;

public partial class LoginAgainDialog : UserControl
{
    public LoginAgainDialog()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Focus after the open animation has attached the popup host. The field is usually pre-filled with the
        // stored password: select it so typing replaces it instead of being inserted in front of it.
        Dispatcher.UIThread.Post(() =>
        {
            PasswordField.Focus();
            PasswordField.SelectAll();
        }, DispatcherPriority.Input);
    }
}
