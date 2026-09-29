using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.View;

namespace NebulaAuth.ViewModel;

public partial class MainVM : ObservableObject
{
    [RelayCommand]
    private Task OpenSetPasswordsDialog()
    {
        return DialogsController.ShowSetAccountsPasswordDialog();
    }

    [RelayCommand]
    private Task OpenExporterDialog()
    {
        return DialogsController.ShowExportDialog();
    }

    [RelayCommand]
    public Task LinkAccount()
    {
        return DialogsController.ShowLinkerDialog();
    }

    [RelayCommand]
    private async Task OpenLinksView()
    {
        DialogService.CloseOnClickAway = true;
        var view = new LinksView();
        await DialogService.Show(view);
        DialogService.CloseOnClickAway = false;
    }
}