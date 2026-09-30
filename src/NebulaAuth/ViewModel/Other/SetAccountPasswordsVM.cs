using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles;
using SteamLibForked.Models.SteamIds;

namespace NebulaAuth.ViewModel.Other;

public partial class SetAccountPasswordsVM : ObservableObject
{
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SetPasswordsCommand))]
    private string? _accountsPasswords;

    /// <summary>
    ///     Steam passwords can be saved only in an encrypted storage (separate encrypted password store, by Steam login).
    /// </summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SetPasswordsCommand))]
    private bool _canSavePasswords = SteamPasswords.CanSave;

    /// <summary>
    ///     The storage is encrypted, but saving Steam passwords is turned off in the settings (other hint, same link).
    /// </summary>
    [ObservableProperty] private bool _saveTurnedOff = SteamPasswords.IsTurnedOff;

    [ObservableProperty] private string? _tip;

    /// <summary>
    ///     Closes this dialog and opens Settings → Encryption, where encryption is enabled.
    /// </summary>
    [RelayCommand]
    private async Task OpenEncryptionSettings()
    {
        DialogService.Close();
        // Only one dialog at a time: let the closed one end first
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await DialogsController.ShowSettingsDialog(SettingsVM.ENCRYPTION_TAB);
    }

    [RelayCommand(CanExecute = nameof(SetPasswordsCanExecute))]
    private void SetPasswords()
    {
        Tip = null;
        var input = AccountsPasswords;
        if (string.IsNullOrWhiteSpace(input)) return;
        var lines = input.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        var success = 0;
        var errors = 0;
        var notFound = 0;

        var mafs = Storage.MaFiles.ToList();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var split = line.Split(":", 2);
                if (split.Length != 2)
                {
                    errors++;
                    continue;
                }

                var login = split[0];
                var password = split[1];
                SteamId64? steamId = null;

                if (SteamId64.TryParse(login, out var id64))
                {
                    steamId = id64;
                }

                var maf = steamId != null
                    ? mafs.FirstOrDefault(m => m.SteamId == steamId || m.AccountName == login)
                    : mafs.FirstOrDefault(m => string.Equals(m.AccountName, login, StringComparison.OrdinalIgnoreCase));

                if (maf == null)
                {
                    notFound++;
                    continue;
                }

                if (!SteamPasswords.TrySet(maf.AccountName, password))
                {
                    errors++;
                    continue;
                }

                success++;
            }
            catch
            {
                errors++;
            }
        }

        if (success > 0 && errors == 0 && notFound == 0)
        {
            Tip = string.Format(GetLocalization("Success"), success);
        }
        else
        {
            Tip = string.Format(GetLocalization("PartialSuccess"), success, notFound, errors);
        }
    }

    private bool SetPasswordsCanExecute()
    {
        return !string.IsNullOrWhiteSpace(AccountsPasswords) && CanSavePasswords;
    }

    [RelayCommand]
    private void ClearTip()
    {
        Tip = null;
    }

    private string GetLocalization(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, "SetAccountPasswordsVM", key);
    }
}