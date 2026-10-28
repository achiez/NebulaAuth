using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core.Browser;
using NebulaAuth.Model.Entities;

namespace NebulaAuth.ViewModel;

public partial class MainVM //Embedded Steam browser
{
    /// <summary>
    ///     Opens Steam in the embedded browser, signed in to the account of the context menu (fixed at the click together
    ///     with its proxy). Concurrent: a click while a window is still loading brings it to the front (same account) or asks
    ///     to close it (another account).
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenInBrowser(object? mafile)
    {
        return mafile is Mafile maf ? SteamBrowser.OpenAsync(maf) : Task.CompletedTask;
    }
}
