using AchiesUtilities.Web.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamLib.Abstractions;
using SteamLib.Api.Services;
using SteamLib.Core;
using SteamLib.Core.StatusCodes;
using SteamLib.Exceptions;
using SteamLib.Exceptions.Authorization;
using SteamLib.ProtoCore.Enums;
using SteamLib.ProtoCore.Services;
using SteamLib.Utility;
using SteamLibForked.Abstractions;
using SteamLibForked.Exceptions.Authorization;
using SteamLibForked.Models.Core;
using SteamLibForked.Models.Session;

namespace SteamLib.Authentication.LoginV2;

public class LoginV2Executor //FIXME: logs
{
    private ILoginConsumer Caller { get; }
    private HttpClient HttpClient { get; }
    private ILogger? Logger { get; }
    private IReadOnlyList<IAuthProvider> AuthProviders { get; }
    private string WebsiteId { get; }
    private DeviceDetails DeviceDetails { get; }

    private LoginV2Executor(LoginV2ExecutorOptions options)
    {
        Caller = options.Consumer;
        HttpClient = options.HttpClient;
        Logger = options.Logger;
        AuthProviders = options.AuthProviders;
        WebsiteId = options.WebsiteId ?? throw new NullReferenceException("WebsiteId was null");
        DeviceDetails = options.DeviceDetails ?? throw new NullReferenceException("DeviceDetails was null");
    }


    /// <summary>
    ///     Do log in on <see href="https://steamcommunity.com/">SteamCommunity</see>.<br />
    ///     Some functions require proper SessionId. But <see cref="SessionData" /> contains only SteamCommunity related
    ///     SessionId and some functions on other services may not work
    /// </summary>
    /// <param name="options"></param>
    /// <param name="username"></param>
    /// <param name="password"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="SessionData" /> or <see cref="MobileSessionData" /> depending on which token type is returned</returns>
    /// <exception cref="LoginException"></exception>
    /// <exception cref="SteamStatusCodeException"></exception>
    /// <exception cref="UnsupportedAuthTypeException"></exception>
    public static async Task<ISessionData> DoLogin(LoginV2ExecutorOptions options, string username, string password,
        CancellationToken cancellationToken = default)
    {
        var executor = new LoginV2Executor(options);
        var client = executor.HttpClient;
        var logger = options.Logger;

        var sessionId = SessionIdGenerator.Generate();

        var rsgMsg = new GetPasswordRSAPublicKey_Request
        {
            AccountName = username
        };

        var rsaResp = await AuthenticationServiceApi.GetPasswordRSAPublicKey(client, rsgMsg, cancellationToken);
        logger?.LogTrace("Got RSA");

        var encodedPassword =
            EncryptionHelper.ToBase64EncryptedPassword(rsaResp.PublickKeyExp, rsaResp.PublickKeyMod, password);


        var beginAuthMsg = new BeginAuthSessionViaCredentials_Request
        {
            DeviceFriendlyName = string.Empty,
            AccountName = username,
            EncryptedPassword = encodedPassword,
            EncryptionTimestamp = rsaResp.Timestamp,
            RememberLogin = true,
            PlatformType = executor.DeviceDetails.PlatformType,
            Persistence = 1,
            WebsiteId = executor.WebsiteId,
            DeviceDetails = executor.DeviceDetails
        };

        logger?.LogDebug("Sending encrypted password to Steam");
        BeginAuthSessionViaCredentials_Response beginAuthResp;
        try
        {
            beginAuthResp =
                await AuthenticationServiceApi.BeginAuthSessionViaCredentials(client, beginAuthMsg, cancellationToken);
        }
        catch (SteamStatusCodeException ex)
            when (ex.StatusCode == SteamStatusCode.InvalidPassword)
        {
            throw new LoginException(LoginError.InvalidCredentials);
        }

        logger?.LogDebug("Password accepted");
        var clientId = beginAuthResp.ClientId;
        var steamId = beginAuthResp.Steamid;

        var wantMore = true;


        PollAuthSessionStatus_Response? pollResp = null;
        for (var i = 0; i < 3; i++)
        {
            if (beginAuthResp.AllowedConfirmations.Count > 0 &&
                beginAuthResp.AllowedConfirmations.All(a => a.ConfirmationType != EAuthSessionGuardType.None))
            {
                var t = SelectProvider(options, beginAuthResp.AllowedConfirmations);
                if (!t.HasValue)
                {
                    throw new UnsupportedAuthTypeException(
                        beginAuthResp.AllowedConfirmations.Select(a => a.ConfirmationType).ToArray());
                }

                var provider = t.Value.Item1;
                var guardType = t.Value.Item2;
                logger?.LogDebug("Asking {provider} {guardType} for confirmation", provider.GetType().Name, guardType);
                var model = new UpdateAuthSessionModel(guardType, clientId, steamId);
                await UpdateSessionAndMapException(client, executor.Caller, provider, model, cancellationToken);
            }

            var pollSessionMsg = new PollAuthSessionStatus_Request
            {
                ClientId = clientId,
                RequestId = beginAuthResp.RequestId
            };

            pollResp = await AuthenticationServiceApi.PollAuthSessionStatus(client, pollSessionMsg, cancellationToken);
            if (pollResp.AccessToken != null! && pollResp.RefreshToken != null!)
            {
                break;
            }
        }

        if (pollResp == null)
        {
            throw new LoginException(LoginError.SessionPollingFailed);
        }


        SteamAuthToken refreshToken;
        try
        {
            refreshToken = SteamTokenHelper.Parse(pollResp.RefreshToken);
            if (refreshToken.Type is not (SteamAccessTokenType.Refresh or SteamAccessTokenType.MobileRefresh))
                throw new InvalidOperationException(
                    "Refresh token must be of type Refresh or MobileRefresh. No 'renew' audience found in JWT.");
        }
        catch (ArgumentException ex)
        {
            throw new LoginException(null,
                "Steam returned invalid refresh token or it's schema was unsupported. See inner exception for more details",
                LoginError.UndefinedError, ex);
        }

        var data = new Dictionary<string, string>
        {
            {"nonce", pollResp.RefreshToken},
            {"sessionid", sessionId}
        };

        logger?.LogInformation("Got refresh token. Finalizing log-in");
        var finalize = await client.PostAsync("https://login.steampowered.com/jwt/finalizelogin",
            new FormUrlEncodedContent(data), cancellationToken);
        var finalizeContent = await finalize.EnsureSuccessStatusCode().Content.ReadAsStringAsync(cancellationToken);

        var finalizeResp =
            JsonConvert.DeserializeObject<FinalizeLoginJson>(finalizeContent)!; //We don't want to log sensitive data


        List<SteamAuthToken> tokens = [];
        foreach (var transferInfo in finalizeResp.TransferInfo)
        {
            var transferData = new Dictionary<string, string>
            {
                {"nonce", transferInfo.TransferInfoParams.Nonce},
                {"auth", transferInfo.TransferInfoParams.Auth},
                {"steamID", steamId.ToString()}
            };


            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, transferInfo.Url);
                req.Content = new FormUrlEncodedContent(transferData);
                req.Headers.Referrer = SteamDomains.GetDomainUri(SteamDomain.Store);
                var transferResp = await client.SendAsync(req, cancellationToken);
                var transferContent = await transferResp.ReadAsStringEnsureSuccessAsync(cancellationToken);
                var status = JObject.Parse(transferContent);
                var result = status["result"]?.Value<int>();
                if (result != null)
                    SteamStatusCode.ValidateSuccessOrThrow(result.Value);

                var tokenStr = SteamTokenHelper.ExtractJwtFromSetCookiesHeader(transferResp.Headers);
                var token = SteamTokenHelper.Parse(tokenStr);
                tokens.Add(token);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error while transferring tokens for URI: {uri}", transferInfo.Url);
            }
        }

        var accessToken = SteamTokenHelper.Parse(pollResp.AccessToken);

        logger?.LogInformation("Login successful. Got {authTokenType}", accessToken.Type);
        if (accessToken.Type == SteamAccessTokenType.Mobile)
        {
            return new MobileSessionData(sessionId, SteamId.FromSteam64(steamId), refreshToken, accessToken, tokens);
        }

        return new SessionData(sessionId, SteamId.FromSteam64(steamId), refreshToken, tokens);
    }

    private static async Task UpdateSessionAndMapException(HttpClient client, ILoginConsumer consumer,
        IAuthProvider provider, UpdateAuthSessionModel model, CancellationToken cancellationToken = default)
    {
        //Also DuplicateRequest means invalid steamId/clientId in MobileConf
        try
        {
            await provider.UpdateAuthSession(client, consumer, model, cancellationToken);
        }
        catch (SteamStatusCodeException ex)
            when (IsSupported(ex.StatusCode))
        {
            var loginError = Map(ex.StatusCode);
            throw new LoginException(loginError, ex);
        }

        return;

        bool IsSupported(SteamStatusCode code)
        {
            return code == SteamStatusCode.InvalidLoginAuthCode ||
                   code == SteamStatusCode.InvalidSignature ||
                   code == SteamStatusCode.TwoFactorCodeMismatch;
        }

        LoginError Map(SteamStatusCode code)
        {
            if (code == SteamStatusCode.InvalidLoginAuthCode)
            {
                return LoginError.InvalidEmailAuthCode;
            }

            if (code == SteamStatusCode.InvalidSignature)
            {
                return LoginError.InvalidSharedSecret;
            }

            if (code == SteamStatusCode.TwoFactorCodeMismatch)
            {
                return LoginError.InvalidTwoFactorCode;
            }

            return LoginError.UndefinedError;
        }
    }

    private static (IAuthProvider, EAuthSessionGuardType)? SelectProvider(LoginV2ExecutorOptions options,
        List<AllowedConfirmationMsg> allowed)
    {
        foreach (var guardType in options.PreferredGuardTypes)
        {
            var availableProvider =
                options.AuthProviders.FirstOrDefault(a => a.IsSupportedGuardType(options.Consumer, guardType));
            if (availableProvider != null)
            {
                return (availableProvider, guardType);
            }
        }

        foreach (var allowedConfirmation in allowed)
        {
            var availableProvider = options.AuthProviders.FirstOrDefault(a =>
                a.IsSupportedGuardType(options.Consumer, allowedConfirmation.ConfirmationType));
            if (availableProvider != null)
            {
                return (availableProvider, allowedConfirmation.ConfirmationType);
            }
        }

        return null;
    }
}