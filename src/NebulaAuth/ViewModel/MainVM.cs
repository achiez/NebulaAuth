using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetBrains.Annotations;
using Material.Icons;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Utility;
using NebulaAuth.View.Dialogs;
using SteamLib.SteamMobile;
using SteamLibForked.Exceptions.Authorization;

namespace NebulaAuth.ViewModel;

public partial class MainVM : ObservableObject
{
    [UsedImplicitly] public SnackbarMessageQueue MessageQueue => SnackbarController.MessageQueue;

    public Mafile? SelectedMafile
    {
        get => _selectedMafile;
        set => SetMafile(value);
    }

    public bool IsMafileSelected => SelectedMafile != null;

    [ObservableProperty] private ObservableCollection<Mafile> _maFiles = new(Storage.MaFiles);

    private Mafile? _selectedMafile;

    public MainVM()
    {
        CreateCodeTimer();
        Proxies = new ObservableCollection<MaProxy>(ProxyStorage.Proxies.Select(kvp =>
            new MaProxy(kvp.Key, kvp.Value)));
        Storage.MaFiles.CollectionChanged += MaFilesOnCollectionChanged;
        QueryGroups();
        InitSkippedFiles();
        _importGate.PhaseChanged += OnImportPhaseChanged;
        UpdateManager.CheckForUpdates();
    }

    [RelayCommand]
    public async Task Debug()
    {
    }

    private void SetMafile(Mafile? mafile)
    {
        if (mafile != SelectedMafile)
        {
            _selectedMafile = mafile;
            OnPropertyChanged(nameof(SelectedMafile));
            OnPropertyChanged(nameof(TradeTimerEnabled));
            OnPropertyChanged(nameof(MarketTimerEnabled));
            MaClient.SetAccount(mafile);
            OnPropertyChanged(nameof(ConfirmationsVisible));
            SetProxy(mafile?.Proxy, true);
            OnPropertyChanged(nameof(IsDefaultProxy));
            if (mafile != null) Code = SteamGuardCodeGenerator.GenerateCode(mafile.SharedSecret);
            OnPropertyChanged(nameof(IsMafileSelected));
            RefreshSessionCommand.NotifyCanExecuteChanged();
            LoginAgainCommand.NotifyCanExecuteChanged();
            RemoveAuthenticatorCommand.NotifyCanExecuteChanged();
            ConfirmLoginCommand.NotifyCanExecuteChanged();
            QrLoginCommand.NotifyCanExecuteChanged();
            RemoveProxyCommand.NotifyCanExecuteChanged();
        }
    }


    [RelayCommand(CanExecute = nameof(IsMafileSelected))]
    public async Task LoginAgain()
    {
        var selectedMafile = SelectedMafile;
        if (selectedMafile == null) return;
        var currentPassword = SteamPasswords.Get(selectedMafile.AccountName);
        var loginAgainVm = await DialogsController.ShowLoginAgainDialog(selectedMafile.AccountName, currentPassword);
        if (loginAgainVm == null)
        {
            return;
        }

        var password = loginAgainVm.Password;
        var waitDialog = new WaitLoginDialog();
        var wait = DialogService.Show(waitDialog);
        try
        {
            await MaClient.LoginAgain(selectedMafile, password, loginAgainVm.SavePassword);
            SnackbarController.SendSnackbar(GetLocalization("SuccessfulLogin"));
        }
        catch (LoginException ex)
        {
            SnackbarController.SendSnackbar(ErrorTranslatorHelper.TranslateLoginError(ex.Error),
                TimeSpan.FromSeconds(1.5));
        }
        catch (Exception ex)
            when (ExceptionHandler.Handle(ex))
        {
            Shell.Logger.Error(ex);
        }
        finally
        {
            DialogsController.CloseDialog();
            await wait;
        }
    }

    private void MaFilesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PerformQuery();
    }


    [RelayCommand(CanExecute = nameof(IsMafileSelected))]
    private async Task RefreshSession()
    {
        var selectedMafile = SelectedMafile;
        if (selectedMafile == null) return;
        try
        {
            await MaClient.RefreshSession(selectedMafile);
            SnackbarController.SendSnackbar(GetLocalization("SessionRefreshed"));
        }
        catch (Exception ex) when (ExceptionHandler.Handle(ex))
        {
            Shell.Logger.Error(ex);
        }
    }

    [RelayCommand]
    public async Task MoveAccount()
    {
        await DialogsController.ShowMafileMoverDialog();
    }

    [RelayCommand(CanExecute = nameof(IsMafileSelected))]
    private async Task RemoveAuthenticator()
    {
        var selectedMafile = SelectedMafile;
        if (selectedMafile == null) return;
        if (string.IsNullOrWhiteSpace(selectedMafile.RevocationCode))
        {
            SnackbarController.SendSnackbar(LocManager.GetCommonOrDefault("Error", "Error") + ": " +
                                            GetLocalization("MissingRCode"));
            return;
        }

        try
        {
            if (await DialogsController.ShowConfirmCancelDialog(new ConfirmDialogOptions(
                    GetLocalization("RemoveAuthenticatorTitle"),
                    selectedMafile.AccountName,
                    MaterialIconKind.ShieldRemove,
                    GetLocalization("RemoveAuthenticatorText"),
                    GetLocalization("RemoveAuthenticatorHint"),
                    HintBoxSeverity.Error,
                    GetLocalization("RemoveAuthenticatorConfirm"),
                    IsDanger: true)))
            {
                var result =
                    await SessionHandler.Handle(() => MaClient.RemoveAuthenticator(selectedMafile), selectedMafile);
                SnackbarController.SendSnackbar(
                    result.Success
                        ? GetLocalization("AuthenticatorRemoved")
                        : GetLocalization("AuthenticatorNotRemoved"));

                if (result.Success)
                {
                    Storage.MoveToRemoved(selectedMafile);
                }
            }
            else
            {
                SnackbarController.SendSnackbar(LocManager.GetCommonOrDefault("Canceled", "Canceled"));
            }
        }
        catch (Exception ex)
            when (ExceptionHandler.Handle(ex, handleAllExceptions: true))
        {
            Shell.Logger.Error(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(IsMafileSelected))]
    private async Task ConfirmLogin()
    {
        var selectedMafile = SelectedMafile;
        if (selectedMafile == null) return;
        try
        {
            var res = await SessionHandler.Handle(() => MaClient.ConfirmLoginRequest(selectedMafile), selectedMafile);
            if (res.Success)
            {
                SnackbarController.SendSnackbar($"{GetLocalization("ConfirmLoginSuccess")} {res.IP} ({res.Country})");
            }
            else
            {
                var msg = res.Error switch
                {
                    LoginConfirmationError.NoRequests => GetLocalization("ConfirmLoginFailedNoRequests"),
                    LoginConfirmationError.MoreThanOneRequest =>
                        GetLocalization("ConfirmLoginFailedMoreThanOneRequest"), //TODO
                    _ => throw new ArgumentOutOfRangeException()
                };
                SnackbarController.SendSnackbar(msg);
            }
        }
        catch (Exception ex)
            when (ExceptionHandler.Handle(ex, handleAllExceptions: true))
        {
            Shell.Logger.Error(ex);
        }
    }

    private static string GetLocalization(string key)
    {
        const string locPath = "MainVM";
        return LocManager.GetCodeBehindOrDefault(key, locPath, key);
    }
}