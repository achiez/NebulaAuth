#if DEBUG
using System;
using System.Globalization;
using System.Threading.Tasks;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.MAAC;
using NebulaAuth.Model.QrLogin;
using NebulaAuth.View.Dialogs;
using SteamLib.Core.StatusCodes;
using SteamLib.Exceptions;
using SteamLib.ProtoCore.Enums;
using SteamLib.SteamMobile.Confirmations;

namespace NebulaAuth.ViewModel;

// Debug builds only: fake data for the MainWindow screenshots (NEBULA_MAIN_DEMO, see MainWindow.Preview.cs)
public partial class MainVM
{
    /// <summary>
    ///     Debug builds only (MainWindow, NEBULA_MAIN_DEMO=confirmations): realistic fake confirmations (every template
    ///     type) with placeholder avatars / item icons embedded in Debug builds (Assets/Demo, no network).
    ///     Confirm/cancel of these items fails with the usual error snackbar.
    /// </summary>
    internal void ShowDemoConfirmations()
    {
        const string ASSETS = "avares://NebulaAuth/Assets/Demo/";
        var now = DateTime.Now;

        MarketConfirmation Listing(long id, string name, string price, string image, int minutesAgo)
        {
            return new MarketConfirmation(id, (ulong) id, (ulong) id, "Market listing")
            {
                ItemName = name, PriceString = price, ItemImageUri = ASSETS + image, Time = now.AddMinutes(-minutesAgo)
            };
        }

        Confirmation[] demo =
        [
            new MarketMultiConfirmation([
                Listing(2, "AK-47 | Redline (Field-Tested)", "12,34€", "item-rifle.png", 2),
                Listing(3, "AWP | Asiimov (Field-Tested)", "98,50€", "item-sniper.png", 2),
                Listing(4, "Operation Breakout Weapon Case", "0,54€", "item-case.png", 3)
            ]),
            new TradeConfirmation(1, 1, 1, "Trade offer")
            {
                UserName = "achies", Time = now.AddMinutes(-9), UserAvatarUri = ASSETS + "avatar-1.png"
            },
            new TradeConfirmation(9, 9, 9, "Trade offer")
            {
                UserName = "kseniya_s", IsReceiveNothing = true, Time = now.AddMinutes(-26),
                UserAvatarUri = ASSETS + "avatar-2.png"
            },
            Listing(5, "Sticker | Crown (Foil)", "610,00€", "item-sticker.png", 64),
            new AccountRecoveryConfirmation(6, 6, 6, "Account recovery") {Time = now.AddMinutes(-165)},
            new RegisterApiKeyConfirmation(7, 7, 7, "Web API key") {Time = now.AddMinutes(-190)},
            new PurchaseConfirmation(8, 8, 8, "Purchase") {Time = now.AddMinutes(-310)},
            new Confirmation(10, 10, 99, 10, ConfirmationType.Unknown, "Unknown type") {Time = now.AddMinutes(-400)}
        ];
        _confirmationsLoadedForMafile = SelectedMafile;
        OnPropertyChanged(nameof(ConfirmationsVisible));
        Confirmations.Clear();
        foreach (var confirmation in demo)
        {
            Confirmations.Add(confirmation);
        }
    }

    /// <summary>
    ///     Debug builds only (MainWindow, NEBULA_MAIN_DEMO=maac): links accounts 2-4 to local auto-confirmation clients with
    ///     the Ok / Warning / Error status (list colors and icons) without starting the confirmation timers.
    /// </summary>
    internal void ShowDemoMaacStatuses()
    {
        PortableMaClientStatus[] statuses =
        [
            PortableMaClientStatus.Ok(),
            PortableMaClientStatus.Warning("Demo warning"),
            PortableMaClientStatus.Error("Demo error")
        ];
        for (var i = 0; i < statuses.Length && i + 1 < MaFiles.Count; i++)
        {
            var mafile = MaFiles[i + 1];
            var client = new PortableMaClient(mafile) {AutoConfirmMarket = i != 1, AutoConfirmTrades = i != 0};
            client.SetStatus(statuses[i]);
            mafile.LinkedClient = client;
        }
    }

    /// <summary>
    ///     Debug builds only (MainWindow, NEBULA_MAIN_DEMO=qr / qrrisk / qrminimal / qrexpired): the QR login of the selected
    ///     account up to the first network call. The clipboard image is read, decoded and validated by the real code
    ///     (errors show the real snackbars); then, instead of GetAuthSessionInfo, fake session info fills the real dialog.
    ///     No request is sent: "qrexpired" shows the snackbar of an expired QR code, approving shows a demo snackbar.
    /// </summary>
    internal async Task DemoQrLogin(string scenario)
    {
        var mafile = SelectedMafile;
        if (mafile == null) return;
        var demoDuration = TimeSpan.FromSeconds(30);
        var qr = await QrClipboardReader.ReadAsync();
        if (!qr.Success)
        {
            SnackbarController.SendSnackbar(GetQrLoginErrorText(qr.Error), demoDuration);
            return;
        }

        var clientId = qr.Challenge.ClientId.ToString(CultureInfo.InvariantCulture);
        Shell.Logger.Info("Demo QR login: version {version}, client id ...{tail}", qr.Challenge.Version,
            clientId[^Math.Min(4, clientId.Length)..]);
        if (scenario == "qrexpired")
        {
            var expired = new SteamStatusCodeException(SteamStatusCode.FromEResult(EResult.Expired));
            if (IsQrLoginExpired(expired))
                SnackbarController.SendSnackbar(GetLocalization("QrLoginExpired"), demoDuration);
            return;
        }

        var info = QrLoginDialog.DemoSessionInfo(scenario switch
        {
            "qrrisk" => "risk",
            "qrminimal" => "minimal",
            _ => null
        });
        var approved = await DialogsController.ShowQrLoginDialog(CreateQrLoginDialogVM(mafile, info));
        SnackbarController.SendSnackbar(
            approved ? "Demo: approval not sent (no network)" : LocManager.GetCommonOrDefault("Canceled", "Canceled"),
            demoDuration);
    }
}
#endif