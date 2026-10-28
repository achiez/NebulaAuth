using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using AchiesUtilities.Web.Proxy;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using SteamLib.Api.Mobile;
using SteamLib.Api.Trade;
using SteamLib.Authentication;
using SteamLib.SteamMobile.Confirmations;
using SteamLib.Web;
using SteamLibForked.Abstractions;

namespace NebulaAuth.Model.MAAC;

public partial class PortableMaClient : ObservableObject, IDisposable
{
    private const string LOC_PATH = "MAAC";
    public Mafile Mafile { get; }
    private HttpClient Client { get; }
    private HttpClientHandler ClientHandler { get; }
    private DynamicProxy Proxy { get; }

    private readonly CancellationTokenSource _cts = new();
    [ObservableProperty] private bool _autoConfirmMarket;

    [ObservableProperty] private bool _autoConfirmTrades;
    [ObservableProperty] private PortableMaClientStatus _status = PortableMaClientStatus.Ok();

    public PortableMaClient(Mafile mafile)
    {
        Mafile = mafile;
        Proxy = new DynamicProxy();
        Proxy.SetData(mafile.Proxy?.Data);
        var pair = ClientBuilder.BuildMobileClient(Proxy, mafile.SessionData);
        Client = pair.Client;
        ClientHandler = pair.Handler;
        UpdateCookies(mafile.SessionData);
        Mafile.PropertyChanged += Mafile_PropertyChanged;
        SetStatus(PortableMaClientStatus.Ok());
    }

    private void Mafile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Mafile.SessionData))
        {
            UpdateCookies(Mafile.SessionData);
        }
    }

    private void UpdateCookies(IMobileSessionData? sessionData)
    {
        var newStatus = MAACRequestHandler.ClearErrors(Mafile);
        if (!newStatus.Equals(Status))
            SetStatus(newStatus);

        ClientHandler.CookieContainer.ClearAllCookies();
        ClientHandler.CookieContainer.SetSteamMobileCookies(sessionData);
    }


    public async Task<int> Confirm()
    {
        Proxy.SetData(Mafile.Proxy?.Data ?? MaClient.DefaultProxy);
        var conf = (await GetConfirmations()).ToList();

        var toConfirm = new List<Confirmation>();
        if (AutoConfirmMarket)
        {
            var market = conf.Where(c =>
                c.ConfType is ConfirmationType.MarketSellTransaction or ConfirmationType.Purchase);
            toConfirm.AddRange(market);
        }

        if (AutoConfirmTrades)
        {
            var trade = conf.Where(c => c.ConfType == ConfirmationType.Trade);
            toConfirm.AddRange(trade);
        }

        if (toConfirm.Count == 0) return 0;
        Shell.Logger.Debug("Timer {accountName}: Sending confirmations. Count: {count}", Mafile.AccountName,
            toConfirm.Count);
        var success = await SendConfirmations(toConfirm);
        //TODO: handle success == false
        Shell.Logger.Debug("Timer {accountName}: Confirmation sent: {confirmResult}", Mafile.AccountName, success);
        return toConfirm.Count;
    }


    private async Task<IEnumerable<Confirmation>> GetConfirmations()
    {
        return await SteamMobileConfirmationsApi.GetConfirmations(Client, Mafile, Mafile.SessionData!.SteamId,
            _cts.Token);
    }

    private async Task<bool> SendConfirmations(IEnumerable<Confirmation> confirmations)
    {
        var conf = confirmations.ToList();
        var res = await SteamMobileConfirmationsApi.SendMultipleConfirmations(Client, conf,
            Mafile.SessionData!.SteamId, Mafile, true, _cts.Token);

        if (!res && conf.Any(c => c.ConfType == ConfirmationType.Trade))
        {
            Shell.Logger.Warn("Timer {accountName}: Failed to send trade confirmations. Sending ack",
                Mafile.AccountName);
            await SteamTradeApi.Acknowledge(Client, Mafile.SessionData!.SessionId, _cts.Token);
            await Task.Delay(10, _cts.Token);
        }
        else
        {
            return res;
        }

        return await SteamMobileConfirmationsApi.SendMultipleConfirmations(Client, conf,
            Mafile.SessionData!.SteamId, Mafile, true, _cts.Token);
    }

    public HttpClientHandlerPair Chp()
    {
        return new HttpClientHandlerPair(Client, ClientHandler);
    }

    private static string GetLocalization(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, LOC_PATH, key);
    }

    private string GetTimerPrefix()
    {
        return GetLocalization("TimerPrefix") + Mafile.AccountName + ": ";
    }

    public void SetStatus(PortableMaClientStatus status)
    {
        Dispatcher.UIThread.Invoke(() => Status = status);
        if (status.StatusType == PortableMaClientStatusType.Error)
        {
            Shell.Logger.Warn("Timer {accountName}: disabled due to error.", Mafile.AccountName);
            SnackbarController.SendSnackbar(GetTimerPrefix() + status.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        Mafile.PropertyChanged -= Mafile_PropertyChanged;
        Client.Dispose();
        ClientHandler.Dispose();
    }
}