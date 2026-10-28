using Avalonia.Controls;

namespace NebulaAuth.View.Dialogs;

/// <summary>
///     Progress dialog shown while a login attempt is running (<c>DialogService.Show(new WaitLoginDialog())</c>).
///     SessionHandler closes it by checking <c>DialogService.GetSession()?.Content is WaitLoginDialog</c>.
/// </summary>
public partial class WaitLoginDialog : UserControl
{
    public WaitLoginDialog()
    {
        InitializeComponent();
    }
}
