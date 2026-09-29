#if DEBUG
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class LoginAgainDialog
{
    /// <summary>
    ///     Debug preview: NEBULA_PREVIEW_ARG "warning" shows the no-proxy warning HintBox.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var vm = new LoginAgainVM
        {
            UserName = "demo_user",
            Password = "hunter2",
            SavePassword = false,
            ShowNoProxyWarning = arg == "warning"
        };
        return new LoginAgainDialog { DataContext = vm };
    }
}
#endif
