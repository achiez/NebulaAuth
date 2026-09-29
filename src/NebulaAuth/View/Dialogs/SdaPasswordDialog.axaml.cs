using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NebulaAuth.View.Dialogs;

/// <summary>
///     Prompts for the SDA (Steam Desktop Authenticator) encryption password when importing an SDA-encrypted
///     mafile. DialogsController.ShowSdaPasswordDialog sets the DataContext.
/// </summary>
public partial class SdaPasswordDialog : UserControl
{
    public SdaPasswordDialog()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Focus after the open animation has attached the popup host (same pattern as TextFieldDialog).
        Dispatcher.UIThread.Post(() => PasswordBox.Focus(), DispatcherPriority.Input);
    }
}
