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
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Utility;
using NLog;
using SteamLib;
using SteamLib.Abstractions;
using SteamLib.Api.Services;
using SteamLib.Authentication;
using SteamLib.Authentication.LoginV2;
using SteamLib.Core.StatusCodes;
using SteamLib.Exceptions;
using SteamLib.Exceptions.Authorization;
using SteamLib.Exceptions.Mobile;
using SteamLib.Factory.Helpers;
using SteamLib.ProtoCore.Enums;
using SteamLib.SteamMobile.AuthenticatorLinker;
using SteamLib.Web;
using SteamLibForked.Abstractions;
using SteamLibForked.Abstractions.Linker;
using SteamLibForked.Exceptions.Authorization;
using SteamLibForked.Models.Session;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NebulaAuth.ViewModel.Linker;

public partial class LinkAccountVM : ObservableObject, ISmsCodeProvider, IPhoneNumberProvider, IEmailProvider,
    IDisposable
{
    public const string LOCALIZATION_KEY = "LinkVM";

    private static Logger Logger => Shell.Logger;
    private static ILogger Logger2 => Shell.ExtensionsLogger;

    int IEmailProvider.RetryCount => 3;
    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;
    private readonly DynamicProxy _proxy;
    private CancellationTokenSource _cts = new();
    [ObservableProperty] private LinkAccountStepVM _currentStep;

    [ObservableProperty] private string? _error;

    private KeyValuePair<int, ProxyData>? _lastProxy;

    [ObservableProperty] private string? _tip;

    public LinkAccountVM()
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

        CurrentStep = new LinkAccountAuthStepVM(selectedProxy)
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
            Logger.Error(ex, "Error while linking mafie");
        }
        finally
        {
            _cts.Dispose();
            if (CurrentStep is not LinkAccountAuthStepVM)
            {
                var step = new LinkAccountAuthStepVM(_lastProxy)
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
        if (CurrentStep is not LinkAccountAuthStepVM authStep)
        {
            throw new InvalidOperationException("Current step is not an authentication step.");
        }

        var (login, pass, proxy) = authStep.GetState(_cts.Token);
        _lastProxy = proxy;
        _proxy.SetData(proxy?.Value);
        var log = new LoginV2ExecutorOptions(StaticLoginConsumer.Instance, _client)
        {
            DeviceDetails = DeviceDetailsDefaultBuilder.GetMobileDefaultDevice(),
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
            when (ex.AllowedGuardTypes.Contains(EAuthSessionGuardType.DeviceCode))
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLocalizationOrDefault("CantLogin") + GetLocalizationOrDefault("UnsupportedGuardType");
            return;
        }
        catch (SteamStatusCodeException ex)
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLocalizationOrDefault("CantLogin") +
                    ErrorTranslatorHelper.TranslateSteamStatusCode(ex.StatusCode);
            return;
        }
        catch (LoginException ex)
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLocalizationOrDefault("CantLogin") + ErrorTranslatorHelper.TranslateLoginError(ex.Error);
            return;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Link exception on login");
            Error = GetLocalizationOrDefault("CantLogin") + ex.Message;
            return;
        }

        if (session is not MobileSessionData msd)
        {
            Logger.Error("Link exception on login: session is not MobileSessionData, but {type}",
                session.GetType().Name);
            Error = "Session data is not MobileSession. " + session.GetType();
            return;
        }


        var linkOptions = new LinkOptions(_client, StaticLoginConsumer.Instance, this,
            this, this, Storage.WriteBackup, Logger2);
        var linker = new SteamAuthenticatorLinker(linkOptions);
        MobileDataExtended result;
        try
        {
            result = await linker.LinkAccount(msd);
        }
        catch (AuthenticatorLinkerException ex)
        {
            Logger.Error(ex, "Link exception");
            Error = $"{GetLocalizationCommon("Error")}: {ErrorTranslatorHelper.TranslateLinkerError(ex.Error)}";
            return;
        }
        catch (HttpRequestException ex)
        {
            var msg = ex.StatusCode?.ToString() ?? ex.Message;
            Error = $"{GetLocalizationCommon("RequestError")}: {msg}";
            return;
        }
        catch (SteamStatusCodeException ex)
        {
            Logger.Error(ex, "Link exception");
            Error = GetLocalizationOrDefault("ErrorWithCode") +
                    ErrorTranslatorHelper.TranslateSteamStatusCode(ex.StatusCode);

            return;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Link exception");
            Error = GetLocalizationOrDefault("UnknownError") + ex.Message;
            return;
        }

        var mafile = Mafile.FromMobileDataExtended(result);
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
        await Done(mafile.RevocationCode ?? string.Empty, mafile.SteamId.Steam64.ToString(), login);
    }

    private void SetCurrentStep(LinkAccountStepVM step)
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

    // @formatter:off
    #region Step 2: Email Code

    public bool IsSupportedGuardType(ILoginConsumer consumer, EAuthSessionGuardType type)
    {
        return type == EAuthSessionGuardType.EmailCode;
    }

    // Step 2: Email Code
    public async Task UpdateAuthSession(HttpClient authClient, ILoginConsumer loginConsumer,
        UpdateAuthSessionModel model,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var step = new LinkAccountEmailAuthStepVM();
                SetCurrentStep(step);
                var res = await step.GetResultAsync();
                var req = AuthRequestHelper.CreateEmailCodeRequest(res, model.ClientId, model.SteamId);
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

    #region Step 3: Phone Number

    // Step 3: Phone number
    public Task<long?> GetPhoneNumber(ILoginConsumer caller)
    {
        var step = new LinkAccountPhoneStepVM();
        SetCurrentStep(step);
        return step.GetResultAsync();
    }

    #endregion

    #region Step 4: Confirm Phone

    public Task ConfirmEmailLink(ILoginConsumer caller, EmailConfirmationType confirmationType,
        CancellationToken cancellationToken = default)
    {
        var retry = CurrentStep is LinkAccountConfirmEmailStepVM;
        var step = new LinkAccountConfirmEmailStepVM(retry);
        SetCurrentStep(step);
        return step.GetResultAsync();
    }

    #endregion

    #region Step 5: SMS Code

    public Task<int> GetSmsCode(ILoginConsumer caller, long? phoneNumber, string? phoneHint)
    {
        var step = new LinkAccountSmsStepVM(phoneHint);
        SetCurrentStep(step);
        return step.GetResultAsync();
    }

    #endregion

    #region Step 6: Email Code

    public async ValueTask<string> GetAddAuthenticatorCode(ILoginConsumer caller,
        CancellationToken cancellationToken = default)
    {
        var step = new LinkAccountEmailCodeStepVM();
        SetCurrentStep(step);
        return await step.GetResultAsync();
    }

    #endregion

    #region Step 7: Done

    public async Task Done(string rCode, string steamId, string login)
    {
        var filename = Settings.Instance.UseAccountNameAsMafileName ? login : steamId;
        var step = new LinkAccountDoneStepVM(rCode, filename);
        SetCurrentStep(step);
        await step.GetResultAsync();
    }

    #endregion
    // @formatter:on
}