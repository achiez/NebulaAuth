using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using AchiesUtilities.Web.Proxy;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.QrLogin;
using SteamLib.Api.Mobile;
using SteamLib.Api.Services;
using SteamLib.Authentication;
using SteamLib.Exceptions.Authorization;
using SteamLib.ProtoCore.Services;
using SteamLib.Web;

namespace NebulaAuth.Model;

public static class MaClient
{
    private static HttpClientHandler ClientHandler { get; }
    private static HttpClient Client { get; }
    private static DynamicProxy Proxy { get; }
    public static ProxyData? DefaultProxy { get; set; }


    static MaClient()
    {
        Proxy = new DynamicProxy();
        var pair = ClientBuilder.BuildMobileClient(Proxy, null);
        Client = pair.Client;
        ClientHandler = pair.Handler;
    }


    public static void SetAccount(Mafile? account)
    {
        ClientHandler.CookieContainer.ClearAllCookies();
        if (account == null) return;
        ClientHandler.CookieContainer.SetSteamMobileCookies(account.SessionData);
        Proxy.SetData(account.Proxy?.Data);
    }

    public static Task LoginAgain(Mafile mafile, string password, bool savePassword)
    {
        SetProxy(mafile);
        return SessionHandler.LoginAgain(new HttpClientHandlerPair(Client, ClientHandler), mafile, password,
            savePassword);
    }


    public static Task RefreshSession(Mafile mafile)
    {
        ValidateMafile(mafile, true);
        SetProxy(mafile);
        return SessionHandler.RefreshMobileToken(new HttpClientHandlerPair(Client, ClientHandler), mafile);
    }

    public static Task<RemoveAuthenticator_Response> RemoveAuthenticator(Mafile mafile)
    {
        ValidateMafile(mafile);
        SetProxy(mafile);
        if (mafile.RevocationCode == null)
        {
            throw new InvalidOperationException("This mafile does not have R-Code");
        }

        var token = mafile.SessionData!.GetMobileToken()!;
        return SteamMobileApi.RemoveAuthenticator(Client, token.Value.Token, mafile.RevocationCode);
    }

    private static void SetProxy(Mafile mafile)
    {
        Proxy.SetData(mafile.Proxy?.Data ?? DefaultProxy);
    }

    private static void ValidateMafile(Mafile mafile, bool ignoreAccessToken = false)
    {
        if (mafile.SessionData == null) throw new SessionInvalidException();
        if (mafile.SessionData.RefreshToken.IsExpired)
            throw new SessionPermanentlyExpiredException();

        if (!ignoreAccessToken)
        {
            var access = mafile.SessionData.GetMobileToken();
            if (access == null || access.Value.IsExpired)
                throw new SessionPermanentlyExpiredException();
        }
    }

    public static async Task<LoginConfirmationResult> ConfirmLoginRequest(Mafile mafile)
    {
        ValidateMafile(mafile);
        SetProxy(mafile);
        var token = mafile.SessionData!.GetMobileToken()!.Value;
        var sessions = await AuthenticationServiceApi.GetAuthSessionsForAccount(Client, token.Token);

        if (sessions.ClientIds.Count == 0)
        {
            return new LoginConfirmationResult
            {
                Error = LoginConfirmationError.NoRequests
            };
        }

        if (sessions.ClientIds.Count > 1)
        {
            return new LoginConfirmationResult
            {
                Error = LoginConfirmationError.MoreThanOneRequest
            };
        }

        var clientId = sessions.ClientIds.Single();
        var clientInfo = await AuthenticationServiceApi.GetAuthSessionInfo(Client, token.Token, clientId);
        await SendLoginApproval(mafile, token.Token, 1, clientId);
        return new LoginConfirmationResult
        {
            Country = clientInfo.Country,
            IP = clientInfo.IP,
            Success = true
        };
    }

    /// <summary>
    ///     QR login, step 1: details of the login attempt from the QR code (no confirmation is sent).
    /// </summary>
    public static Task<GetAuthSessionInfo_Response> GetQrLoginInfo(Mafile mafile, SteamQrChallenge challenge)
    {
        ValidateMafile(mafile);
        SetProxy(mafile);
        var token = mafile.SessionData!.GetMobileToken()!.Value;
        return AuthenticationServiceApi.GetAuthSessionInfo(Client, token.Token, challenge.ClientId);
    }

    /// <summary>
    ///     QR login, step 2: approves the login attempt with the version and client id of the QR code.
    ///     A 401 is reported as <see cref="SessionInvalidException" /> so <see cref="SessionHandler" /> refreshes the session.
    /// </summary>
    public static async Task<bool> ApproveQrLogin(Mafile mafile, SteamQrChallenge challenge)
    {
        ValidateMafile(mafile);
        SetProxy(mafile);
        var token = mafile.SessionData!.GetMobileToken()!.Value;
        try
        {
            await SendLoginApproval(mafile, token.Token, challenge.Version, challenge.ClientId);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SessionInvalidException(SessionInvalidException.GOT_401_MSG, ex);
        }

        return true;
    }

    /// <summary>
    ///     UpdateAuthSessionWithMobileConfirmation: confirm = true, persistence = Persistent, signed with the shared secret
    ///     of <paramref name="mafile" />.
    /// </summary>
    private static Task SendLoginApproval(Mafile mafile, string accessToken, int version, ulong clientId)
    {
        var request = AuthRequestHelper.CreateMobileConfirmationRequest(version, clientId,
            mafile.SessionData!.SteamId.Steam64, mafile.SharedSecret);
        return AuthenticationServiceApi.UpdateAuthSessionWithMobileConfirmation(Client, accessToken, request);
    }

    public static HttpClientHandlerPair GetHttpClientHandlerPair(Mafile mafile)
    {
        SetProxy(mafile);
        return new HttpClientHandlerPair(Client, ClientHandler);
    }
}