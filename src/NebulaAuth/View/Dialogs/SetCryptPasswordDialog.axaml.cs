using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NebulaAuth.View.Dialogs;

/// <summary>
///     Prompts for the mafile-password encryption password on startup. The caller
///     (MainWindow.ShowSetPasswordDialog) sets the DataContext.
/// </summary>
public partial class SetCryptPasswordDialog : UserControl
{
    public SetCryptPasswordDialog()
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
