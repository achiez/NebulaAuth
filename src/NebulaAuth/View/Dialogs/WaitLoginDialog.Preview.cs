#if DEBUG
namespace NebulaAuth.View.Dialogs;

public partial class WaitLoginDialog
{
    /// <param name="arg">Optional text that replaces the localized message, to check wrapping of long translations.</param>
    internal static object CreatePreview(string? arg)
    {
        var dialog = new WaitLoginDialog();
        if (!string.IsNullOrEmpty(arg))
            dialog.MessageText.Text = arg;
        return dialog;
    }
}
#endif
