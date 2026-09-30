using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Proxy;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.DeviceModels;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Utility;
using NebulaAuth.ViewModel.Linker;
using Newtonsoft.Json;
using NLog;
using SteamLib.Abstractions;
using SteamLib.Api.Mobile;
using SteamLib.Api.Services;
using SteamLib.Authentication;
using SteamLib.Authentication.LoginV2;
using SteamLib.Core.StatusCodes;
using SteamLib.Exceptions;
using SteamLib.Exceptions.Authorization;
using SteamLib.Factory.Helpers;
using SteamLib.ProtoCore.Enums;
using SteamLib.ProtoCore.Services;
using SteamLib.Web;
using SteamLibForked.Abstractions;
using SteamLibForked.Exceptions.Authorization;
using SteamLibForked.Models.Core;
using SteamLibForked.Models.Session;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NebulaAuth.ViewModel.MafileMover;

public partial class MafileMoverVM : ObservableObject, IAuthProvider, IDisposable
{
    public const string LOCALIZATION_KEY = "MafileMoverVM";

    private static Logger Logger => Shell.Logger;
    private static ILogger Logger2 => Shell.ExtensionsLogger;
    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;
    private readonly DynamicProxy _proxy;
    private CancellationTokenSource _cts = new();
    [ObservableProperty] private MafileMoverStepVM _currentStep;

    [ObservableProperty] private string? _error;

    private KeyValuePair<int, ProxyData>? _lastProxy;

    // Device name of the new authenticator: chosen once before the first request, kept for retries, saved in the mafile
    private string? _deviceName;

    [ObservableProperty] private string? _tip;

    public MafileMoverVM()
    {
        _proxy = new DynamicProxy();
        var pair = ClientBuilder.BuildMobileClient(_proxy, null);
        _client = pair.Client;
        _handler = pair.Handler;
        KeyValuePair<int, ProxyData>? selectedProxy = null;
        if (MaClient.DefaultProxy != null)
        {
            var def = ProxyStorage.Proxies.FirstOrDefault(p => p.Value.Equals(MaClient.DefaultProxy));

            if (def.Value != null!)
            {
                selectedProxy = def;
            }
        }

        CurrentStep = new MafileMoverAuthStepVM(selectedProxy)
        {
            Callback = Proceed
        };
    }

    public async Task Proceed()
    {
        _cts = new CancellationTokenSource();
        _cts.Token.Register(() => { CurrentStep.Cancel(); });
        _handler.CookieContainer.ClearMobileSessionCookies();


        try
        {
            await ProceedImpl();
        }
        catch (OperationCanceledException)
        {
            //Ignored
        }
        catch (Exception ex)
        {
            Error = ExceptionHandler.GetExceptionString(ex);
            Logger.Error(ex, "Error while moving mafie");
        }
        finally
        {
            _cts.Dispose();
            if (CurrentStep is not MafileMoverAuthStepVM)
            {
                var step = new MafileMoverAuthStepVM(_lastProxy)
                {
                    Callback = Proceed
                };
                SetCurrentStep(step);
            }
        }
    }

    private async Task ProceedImpl()
    {
        // Step 1: login/pass
        Error = null;
        if (CurrentStep is not MafileMoverAuthStepVM authStep)
        {
            throw new InvalidOperationException("Current step is not an authentication step.");
        }

        var (login, pass, proxy) = authStep.GetState(_cts.Token);
        _lastProxy = proxy;
        _proxy.SetData(proxy?.Value);
        _deviceName ??= DeviceNames.PickRandom();
        var log = new LoginV2ExecutorOptions(StaticLoginConsumer.Instance, _client)
        {
            DeviceDetails = DeviceDetailsDefaultBuilder.GetMobileDevice(_deviceName),
            WebsiteId = "Mobile",
            AuthProviders = [this],
            Logger = Logger2
        };

        ISessionData session;
        try
        {
            session = await LoginV2Executor.DoLogin(log, login, pass);
        }
        catch (UnsupportedAuthTypeException ex)
            when (!ex.AllowedGuardTypes.Contains(EAuthSessionGuardType.DeviceCode))
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLinkerLocalizationOrDefault("CantLogin") + GetLocalizationOrDefault("GuardIsNotActive");
            return;
        }
        catch (SteamStatusCodeException ex)
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLinkerLocalizationOrDefault("CantLogin") +
                    ErrorTranslatorHelper.TranslateSteamStatusCode(ex.StatusCode);
            return;
        }
        catch (LoginException ex)
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLinkerLocalizationOrDefault("CantLogin") + ErrorTranslatorHelper.TranslateLoginError(ex.Error);
            return;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLinkerLocalizationOrDefault("CantLogin") + ex.Message;
            return;
        }

        if (session is not MobileSessionData msd)
        {
            Logger.Error("Link exception on login: session is not MobileSessionData, but {type}",
                session.GetType().Name);
            Error = "Session data is not MobileSession. " + session.GetType();
            return;
        }

        if (CurrentStep is not MafileMoverGuardCodeStepVM)
        {
            //No guard was requested
            Error = GetLocalizationOrDefault("GuardIsNotActive");
            return;
        }

        var token = msd.GetToken(SteamDomain.Community)!.Value.Token;
        try
        {
            await SteamAuthenticatorLinkerApi.RemoveAuthenticatorViaChallengeStart(_client, token, _cts.Token);
        }
        catch (SteamStatusCodeException ex)
            when (ex.StatusCode.Equals(SteamStatusCode.Fail))
        {
            Logger.Error(ex, "Move mafile exception via challenge continue");
            Error = GetLocalizationOrDefault("SeemsNoPhoneNumber");
            return;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Move mafile exception via challenge start");
            Error = ExceptionHandler.GetExceptionString(ex);
            return;
        }


        RemoveAuthenticatorViaChallengeContinue_Response res;
        try
        {
            var i = 0;
            while (true)
            {
                try
                {
                    var sms = await GetSms();
                    res = await SteamAuthenticatorLinkerApi
                        .RemoveAuthenticatorViaChallengeContinue(_client, token, sms.ToString("D5"));
                    Error = null;
                    break;
                }
                catch (SteamStatusCodeException ex)
                    when (ex.StatusCode.Equals(SteamStatusCode.SmsCodeFailed))
                {
                    Logger.Warn("Sms code failed, retrying {attempt} time", i + 1);
                    Error = GetLocalizationOrDefault("SmsCodeFailed");
                    if (i == 2) throw;
                }

                i++;
            }
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Move mafile exception via challenge continue");
            Error = ExceptionHandler.GetExceptionString(ex);
            return;
        }

        var t = res.ReplacementToken;
        var j = JsonConvert.SerializeObject(t);
        Storage.WriteBackup(login, j);
        var mobileData = t.ToMobileDataExtended(SteamAuthenticatorLinkerApi.GenerateDeviceId(), msd);


        var mafile = Mafile.FromMobileDataExtended(mobileData);
        mafile.DeviceName = _deviceName;
        try
        {
            if (proxy.HasValue)
                mafile.Proxy = new MaProxy(proxy.Value.Key, proxy.Value.Value);
            // Kept only in an encrypted storage with saving allowed (the login step tells the user); otherwise nothing is saved
            SteamPasswords.TrySet(mafile.AccountName, pass);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during saving Nebula data to mafile");
        }

        await Storage.SaveMafileAsync(mafile);
        await Done(mafile.RevocationCode ?? string.Empty, mafile.SteamId.Steam64.ToString());
    }

    private void SetCurrentStep(MafileMoverStepVM step)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            CurrentStep = step;
            Tip = CurrentStep.Tip;
        });
    }

    [RelayCommand]
    private void OpenTroubleshooting()
    {
        const string troubleshootingURI =
            "https://achiez.github.io/NebulaAuth-Steam-Desktop-Authenticator-by-Achies/docs/{0}/LinkingTroubleshooting";

        var localized = string.Format(troubleshootingURI, LocManager.GetCurrentLanguageCode());
        PlatformUtils.OpenUrl(new Uri(localized).ToString());
    }

    private static string GetLocalizationOrDefault(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, LOCALIZATION_KEY, key);
    }

    private static string GetLinkerLocalizationOrDefault(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, LinkAccountVM.LOCALIZATION_KEY, key);
    }

    private static string GetLocalizationCommon(string key)
    {
        return LocManager.GetCommonOrDefault(key, key);
    }


    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
        try
        {
            _cts.Cancel();
        }
        catch
        {
            //Ignored, may be cancelled or disposed
        }

        _cts.Dispose();
    }


    public bool IsSupportedGuardType(ILoginConsumer consumer, EAuthSessionGuardType type)
    {
        return type == EAuthSessionGuardType.DeviceCode;
    }

    // @formatter:off

    #region Step 2: Guard Code
    // Step 2: Guard Code
    public async Task UpdateAuthSession(HttpClient authClient, ILoginConsumer loginConsumer,
        UpdateAuthSessionModel model,
        CancellationToken cancellationToken = default)
    {
        var isRetrying = CurrentStep is MafileMoverGuardCodeStepVM;
        while (true)
        {
            try
            {
                var step = new MafileMoverGuardCodeStepVM(isRetrying);
                SetCurrentStep(step);
                var res = await step.GetResultAsync();
                if (res == null)
                {
                    return;
                }

                var req = AuthRequestHelper.CreateMobileCodeRequest(res, model.ClientId, model.SteamId);
                await AuthenticationServiceApi.UpdateAuthSessionWithSteamGuardCode(authClient, req, cancellationToken);
                Error = null;
                break;
            }
            catch (SteamStatusCodeException ex)
                when (ex.StatusCode.Equals(SteamStatusCode.InvalidLoginAuthCode))
            {
                Error = ExceptionHandler.GetExceptionString(ex);
            }
        }
    }

    #endregion

    #region Step 3: Sms code

    // Step 3: Sms code
    public Task<int> GetSms()
    {
        var step = new MafileMoverSmsStepVM();
        SetCurrentStep(step);
        return step.GetResultAsync();
    }

    #endregion

    #region Step 4: Done

    public async Task Done(string rCode, string steamId)
    {
        var step = new MafileMoverDoneStepVM(rCode, steamId);
        SetCurrentStep(step);
        await step.GetResultAsync();
    }

    #endregion

    // @formatter:on
}