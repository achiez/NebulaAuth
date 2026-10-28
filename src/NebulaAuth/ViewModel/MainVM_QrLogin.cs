using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.QrLogin;
using NebulaAuth.Utility;
using NebulaAuth.ViewModel.Other;
using SteamLib.Exceptions;
using SteamLib.ProtoCore.Enums;
using SteamLib.ProtoCore.Services;

namespace NebulaAuth.ViewModel;

public partial class MainVM //QR login
{
    /// <summary>
    ///     Steam results of GetAuthSessionInfo / UpdateAuthSessionWithMobileConfirmation for a login attempt that no longer
    ///     exists or cannot be approved anymore (expired, denied, already approved, unknown client id).
    /// </summary>
    private static readonly EResult[] QrLoginExpiredResults =
        [EResult.FileNotFound, EResult.Expired, EResult.NoMatch, EResult.DuplicateRequest];

    /// <summary>
    ///     Confirms a Steam login from the QR code screenshot on the clipboard with the account selected when the command
    ///     starts (switching the account later does not change the approver). The QR code is read locally; Steam is only
    ///     contacted for a valid Steam login QR code: session info first, the approval after the user confirmed it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsMafileSelected))]
    private async Task QrLogin()
    {
        var mafile = SelectedMafile;
        if (mafile == null) return;

        var qr = await QrClipboardReader.ReadAsync();
        if (!qr.Success)
        {
            Shell.Logger.Info("QR login for {name}: no Steam login QR code on the clipboard ({error})",
                mafile.AccountName, qr.Error);
            SnackbarController.SendSnackbar(GetQrLoginErrorText(qr.Error));
            return;
        }

        var challenge = qr.Challenge;
        Shell.Logger.Info("QR login for {name}: QR code read (version {version})", mafile.AccountName,
            challenge.Version);
        try
        { 
            var info = await SessionHandler.Handle(() => MaClient.GetQrLoginInfo(mafile, challenge), mafile);
            if (!await DialogsController.ShowQrLoginDialog(CreateQrLoginDialogVM(mafile, info)))
            {
                Shell.Logger.Info("QR login for {name} canceled by the user", mafile.AccountName);
                SnackbarController.SendSnackbar(LocManager.GetCommonOrDefault("Canceled", "Canceled"));
                return;
            }

            await SessionHandler.Handle(() => MaClient.ApproveQrLogin(mafile, challenge), mafile);
            Shell.Logger.Info("QR login for {name} approved", mafile.AccountName);
            SnackbarController.SendSnackbar(GetLocalization("QrLoginConfirmed"));
        }
        catch (SteamStatusCodeException ex) when (IsQrLoginExpired(ex))
        {
            Shell.Logger.Warn("QR login for {name}: the login attempt is expired or invalid ({code})",
                mafile.AccountName, ex.StatusCode);
            SnackbarController.SendSnackbar(GetLocalization("QrLoginExpired"));
        }
        catch (Exception ex)
            when (ExceptionHandler.Handle(ex, handleAllExceptions: true))
        {
            Shell.Logger.Error(ex);
        }
    }

    private static QrLoginDialogVM CreateQrLoginDialogVM(Mafile mafile, GetAuthSessionInfo_Response info)
    {
        var steamId = (mafile.SessionData?.SteamId ?? mafile.SteamId).Steam64.ToUlong();
        return new QrLoginDialogVM(mafile.AccountName, steamId == 0 ? null : steamId.ToString(), info);
    }

    private static bool IsQrLoginExpired(SteamStatusCodeException ex)
    {
        return Array.IndexOf(QrLoginExpiredResults, (EResult) ex.StatusCode.Id) >= 0;
    }

    private static string GetQrLoginErrorText(QrClipboardError error)
    {
        return error switch
        {
            QrClipboardError.ClipboardEmpty => GetLocalization("QrLoginClipboardEmpty"),
            QrClipboardError.NoImage => GetLocalization("QrLoginNoImage"),
            QrClipboardError.InvalidImage => GetLocalization("QrLoginInvalidImage"),
            QrClipboardError.NoQrCode => GetLocalization("QrLoginNoQrCode"),
            QrClipboardError.MultipleQrCodes => GetLocalization("QrLoginMultipleQrCodes"),
            QrClipboardError.NotSteamQrCode => GetLocalization("QrLoginNotSteamQrCode"),
            _ => LocManager.GetCommonOrDefault("Error", "Error")
        };
    }
}
