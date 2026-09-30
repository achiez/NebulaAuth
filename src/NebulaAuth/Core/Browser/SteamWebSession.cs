using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using AchiesUtilities.Web.Proxy;
using NebulaAuth.Model;
using NebulaAuth.Model.Browser;
using NebulaAuth.Model.Entities;
using NebulaAuth.Utility;
using SteamLib.Api.Services;
using SteamLib.Authentication;
using SteamLib.Exceptions.Authorization;
using SteamLib.Web;
using SteamLibForked.Models.Session;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     Prepares the Steam web session of one account for the embedded browser: makes sure the mobile session is valid
///     through the usual <see cref="SessionHandler" /> path (refresh, saved-password sign-in or the sign-in dialog) and
///     builds the browser cookies from it.
///     <para>
///         Uses its own HTTP client with the proxy fixed when the window was opened (not the shared <see cref="MaClient" />
///         client, whose proxy and cookies follow the selected account).
///     </para>
///     <para>
///         The browser gets only <c>steamLoginSecure</c> (the mobile access token: its "web" audience is accepted by the
///         Steam web domains, verified by a real sign-in check), <c>sessionid</c> and <c>Steam_Language</c>. Not given to
///         the page: the refresh token (<c>steamRefresh_steam</c>) and the mobile app markers (<c>mobileClient</c>,
///         <c>mobileClientVersion</c>).
///     </para>
///     <para>
///         Fallback if Steam ever stops accepting the mobile token on web domains: <c>login.steampowered.com/jwt/finalizelogin</c>
///         with the refresh token as nonce returns per-domain web tokens (transfer_info); it worked in the engine prototype
///         but is not used, because it creates additional web sessions for the account.
///     </para>
/// </summary>
internal static class SteamWebSession
{
    private static readonly string[] BrowserCookieNames =
        [AdmissionHelper.ACCESS_COOKIE_NAME, AdmissionHelper.SESSION_ID_COOKIE_NAME, AdmissionHelper.LANGUAGE_COOKIE_NAME];

    /// <summary>
    ///     Validates (and if needed renews) the session of <paramref name="mafile" />; returns a snapshot of it.
    /// </summary>
    /// <exception cref="BrowserOpenException">No usable session (sign-in failed or cancelled), network error.</exception>
    public static async Task<MobileSessionData> EnsureSessionAsync(Mafile mafile, UpstreamProxy? proxy)
    {
        var pair = ClientBuilder.BuildMobileClient(proxy == null ? null : new DynamicProxy(proxy.ToProxyData()), null);
        using var client = pair.Client;
        try
        {
            var chp = new HttpClientHandlerPair(client, pair.Handler);
            var session = await SessionHandler.Handle(() => CheckSessionAsync(mafile, chp), mafile, chp);
            return session.Clone();
        }
        catch (SessionPermanentlyExpiredException ex)
        {
            throw new BrowserOpenException(BrowserError.SessionUnavailable, "No valid session", ex);
        }
        catch (SessionInvalidException ex)
        {
            throw new BrowserOpenException(BrowserError.SessionUnavailable, "Session invalid", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BrowserOpenException(BrowserError.SteamUnreachable, "Steam API request failed", ex)
            {
                UserDetail = ExceptionHandler.GetExceptionString(ex)
            };
        }
        catch (Exception ex) when (ex is not BrowserOpenException)
        {
            throw new BrowserOpenException(BrowserError.SessionUnavailable, "Session check failed", ex)
            {
                UserDetail = ExceptionHandler.GetExceptionString(ex)
            };
        }
    }

    /// <summary>
    ///     Read-only request with the mobile access token (pending sign-in requests of the account): a revoked token gives
    ///     401 = <see cref="SessionInvalidException" />, which makes <see cref="SessionHandler" /> renew the session.
    /// </summary>
    private static async Task<MobileSessionData> CheckSessionAsync(Mafile mafile, HttpClientHandlerPair chp)
    {
        var session = mafile.SessionData ?? throw new SessionInvalidException(SessionInvalidException.SESSION_NULL_MSG);
        var token = session.GetMobileToken();
        if (token == null || token.Value.IsExpired) throw new SessionInvalidException();
        await AuthenticationServiceApi.GetAuthSessionsForAccount(chp.Client, token.Value.Token);
        return session;
    }

    /// <summary>
    ///     Browser cookies of <paramref name="session" />: the Steam web domains get the access token; sessionid and the
    ///     language as the app's own client sets them.
    /// </summary>
    public static List<BrowserCookie> BuildCookies(MobileSessionData session, string steamLanguage)
    {
        var container = new CookieContainer();
        container.SetSteamMobileCookies(session, steamLanguage);
        var result = new List<BrowserCookie>();
        foreach (Cookie c in container.GetAllCookies())
        {
            if (c.Expired || Array.IndexOf(BrowserCookieNames, c.Name) < 0) continue;
            var isToken = c.Name == AdmissionHelper.ACCESS_COOKIE_NAME;
            result.Add(new BrowserCookie(c.Name, c.Value, c.Domain.TrimStart('.'),
                string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                Secure: true,
                HttpOnly: isToken || c.HttpOnly,
                Expires: c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires)));
        }

        return result;
    }

    /// <summary>Steam site language for the app language (Steam_Language cookie).</summary>
    public static string SteamLanguage(string appLanguageCode)
    {
        return appLanguageCode switch
        {
            "ru" => "russian",
            "uk" => "ukrainian",
            "zh" => "schinese",
            "fr" => "french",
            "es" => "spanish",
            "tr" => "turkish",
            _ => "english"
        };
    }

    /// <summary>Accept-Language of the engine for the app language.</summary>
    public static string EngineLanguage(string appLanguageCode)
    {
        return appLanguageCode switch
        {
            "ru" => "ru-RU",
            "uk" => "uk-UA",
            "zh" => "zh-CN",
            "fr" => "fr-FR",
            "es" => "es-ES",
            "tr" => "tr-TR",
            "kk" => "kk-KZ",
            _ => "en-US"
        };
    }
}
