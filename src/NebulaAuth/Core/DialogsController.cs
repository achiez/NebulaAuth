using System.Collections.Generic;
using System.Threading.Tasks;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Update;
using NebulaAuth.View;
using NebulaAuth.View.Dialogs;
using NebulaAuth.ViewModel.Linker;
using NebulaAuth.ViewModel.MafileMover;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.Core;

public static class DialogsController
{
    public static async Task<LoginAgainVM?> ShowLoginAgainDialog(string username, string? currentPassword = null,
        bool showNoProxyWarning = false)
    {
        var vm = new LoginAgainVM
        {
            UserName = username,
            Password = currentPassword ?? string.Empty,
            SavePassword = PHandler.IsPasswordSet,
            ShowNoProxyWarning = showNoProxyWarning
        };
        var content = new LoginAgainDialog
        {
            DataContext = vm
        };
        var result = await DialogService.Show(content);
        if (result is true)
        {
            return vm;
        }

        return null;
    }

    public static async Task<LoginAgainOnImportVM?> ShowLoginAgainOnImportDialog(Mafile mafile,
        IEnumerable<MaProxy> proxies)
    {
        var vm = new LoginAgainOnImportVM(mafile, proxies);
        var content = new LoginAgainOnImportDialog
        {
            DataContext = vm
        };
        var result = await DialogService.Show(content);
        if (result is true)
        {
            return vm;
        }

        return null;
    }

    /// <summary>
    ///     Shows the SDA encryption password dialog for unlocking SDA-encrypted mafiles.
    /// </summary>
    /// <returns>The entered password if user clicked OK, otherwise null.</returns>
    public static async Task<string?> ShowSdaPasswordDialog()
    {
        var vm = new SdaPasswordDialogVM();
        var content = new SdaPasswordDialog
        {
            DataContext = vm
        };
        var result = await DialogService.Show(content);
        if (result is true)
        {
            return vm.Password;
        }

        return null;
    }

    public static async Task<MafileImportDialogResult?> ShowMafileImportDialog(IEnumerable<string> groups,
        int totalCount, int conflictCount)
    {
        var vm = new MafileImportDialogVM(groups, totalCount, conflictCount);
        var content = new MafileImportDialog
        {
            DataContext = vm
        };
        var result = await DialogService.Show(content);
        return result is MafileImportDialogVM
            ? new MafileImportDialogResult(
                !vm.AddToGroup || string.IsNullOrWhiteSpace(vm.Group) ? null : vm.Group.Trim(),
                vm.OverwriteConflicts)
            : null;
    }

    public static async Task<bool> ShowProxyManager()
    {
        var vm = new ProxyManagerVM();
        var view = new ProxyManagerView
        {
            DataContext = vm
        };
        await DialogService.Show(view);
        return vm.AnyChanges;
    }

    public static void CloseDialog()
    {
        DialogService.Close();
    }

    public static async Task ShowLinkerDialog()
    {
        LinkAccountVM? vm = null;
        try
        {
            vm = new LinkAccountVM();
            var view = new LinkerView
            {
                DataContext = vm
            };
            await DialogService.Show(view);
        }
        finally
        {
            vm?.Dispose();
        }
    }

    public static async Task ShowMafileMoverDialog()
    {
        MafileMoverVM? vm = null;
        try
        {
            vm = new MafileMoverVM();
            var view = new MafileMoverView
            {
                DataContext = vm
            };
            await DialogService.Show(view);
        }
        finally
        {
            vm?.Dispose();
        }
    }

    public static async Task ShowSetAccountsPasswordDialog()
    {
        var vm = new SetAccountPasswordsVM();
        var dialog = new SetAccountPasswordsView
        {
            DataContext = vm
        };
        await DialogService.Show(dialog);
    }

    public static async Task ShowExportDialog()
    {
        MafileExporterVM? vm = null;
        try
        {
            vm = new MafileExporterVM();
            var dialog = new MafileExporterView
            {
                DataContext = vm
            };
            await DialogService.Show(dialog);
        }
        finally
        {
            vm?.Dispose();
        }
    }

    public static async Task ShowUpdateDialog(UpdateInfo args, ChangelogEntry? changelog,
        string? htmlFallbackUrl)
    {
        var vm = new UpdateDialogVM(args, changelog, htmlFallbackUrl);
        var dialog = new UpdateDialog
        {
            DataContext = vm
        };
        await DialogService.Show(dialog);
    }

    #region CommonDialogs

    public static async Task<bool> ShowConfirmCancelDialog(string? msg = null)
    {
        var content = msg == null ? new ConfirmCancelDialog() : new ConfirmCancelDialog(msg);

        var result = await DialogService.Show(content);
        return result != null && (bool) result;
    }

    /// <summary>
    ///     Confirmation with a description, a hint callout (severity) and optionally a red confirm button.
    /// </summary>
    public static async Task<bool> ShowConfirmCancelDialog(ConfirmDialogOptions options)
    {
        var result = await DialogService.Show(new ConfirmCancelDialog(options));
        return result is true;
    }

    public static async Task<string?> ShowTextFieldDialog(string? title = null, string? msg = null)
    {
        var content = new TextFieldDialog(title, msg);
        var result = await DialogService.Show(content);
        return result as string;
    }

    #endregion
}