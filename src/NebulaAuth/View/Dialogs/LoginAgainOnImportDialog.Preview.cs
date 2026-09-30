#if DEBUG
using System.Collections.Generic;
using AchiesUtilities.Web.Proxy;
using NebulaAuth.Model.Entities;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class LoginAgainOnImportDialog
{
    /// <summary>
    ///     Debug preview: NEBULA_PREVIEW_ARG "mafile-proxy" starts with the mafile's own proxy pre-selected
    ///     (UseMafileProxy = true), otherwise a manual proxy list is shown with none selected; "save" (comma separated)
    ///     shows the "save password" option of an encrypted storage, "off" the hint of saving turned off in the settings.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var proxies = new List<MaProxy>
        {
            new(1, new ProxyData(ProxyProtocol.HTTP, "127.0.0.1", 8080, null, null)),
            new(2, new ProxyData(ProxyProtocol.SOCKS5, "10.0.0.5", 1080, "demo", "demo"))
        };

        var mafile = new Mafile { AccountName = "demo_user" };
        if (arg?.Contains("mafile-proxy") == true)
        {
            mafile.Proxy = proxies[0];
        }

        var vm = new LoginAgainOnImportVM(mafile, proxies)
        {
            Password = "hunter2",
            CanSavePassword = arg?.Contains("save") == true,
            SaveTurnedOff = arg?.Contains("off") == true
        };
        return new LoginAgainOnImportDialog { DataContext = vm };
    }
}
#endif
