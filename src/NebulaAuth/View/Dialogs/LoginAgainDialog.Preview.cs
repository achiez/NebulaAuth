#if DEBUG
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class LoginAgainDialog
{
    /// <summary>
    ///     Debug preview: NEBULA_PREVIEW_ARG (comma separated) "warning" shows the no-proxy warning HintBox, "save" the
    ///     "save password" option of an encrypted storage (otherwise the "saving requires encryption" hint), "off" the hint of saving turned off in the settings.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var vm = new LoginAgainVM
        {
            UserName = "demo_user",
            // "nonlatin": a Cyrillic password (keyboard layout hint under the field)
            Password = arg?.Contains("nonlatin") == true ? "хантер2" : "hunter2",
            SavePassword = arg?.Contains("save") == true,
            CanSavePassword = arg?.Contains("save") == true,
            SaveTurnedOff = arg?.Contains("off") == true,
            ShowNoProxyWarning = arg?.Contains("warning") == true
        };
        return new LoginAgainDialog { DataContext = vm };
    }
}
#endif
