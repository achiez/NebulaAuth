using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.NebulaLink.Sessions;
using SteamLib;
using SteamLib.Api.Mobile;
using SteamLib.Api.Trade;
using SteamLib.Exceptions.Authorization;
using SteamLib.SteamMobile.Confirmations;

namespace NebulaAuth.NebulaLink.Confirmations;

/// <summary>Steam list / send calls of the confirmation service (a seam for tests).</summary>
public interface IConfirmationTransport
{
    /// <summary>Key of the proxy the account uses (per-proxy fetch concurrency).</summary>
    string ProxyKey(MobileDataExtended mafile);

    /// <summary>
    ///     The current list. Interactive: original exceptions (the manual screen shows them). Headless: failures are
    ///     <see cref="NebulaCodeException" />.
    /// </summary>
    Task<IReadOnlyList<Confirmation>> FetchAsync(MobileDataExtended mafile, SessionMode mode, CancellationToken ct);

    /// <summary>
    ///     Accepts / declines the items (one ajaxop for a single item, multiajaxop for several). Interactive: failures are
    ///     thrown as they are. Headless: failures are returned as <see cref="TransportSendStatus" />.
    /// </summary>
    Task<TransportSendResult> SendAsync(MobileDataExtended mafile, IReadOnlyList<Confirmation> items, bool accept, SessionMode mode,
        CancellationToken ct);
}

/// <summary>
///     Session handling of the manual confirmations screen, supplied by the app (it may show dialogs: re-login, password).
/// </summary>
public interface IInteractiveSessionRunner
{
    Task<T> RunAsync<T>(Func<Task<T>> func, MobileDataExtended mafile, HttpClientHandlerPair chp);
}

/// <summary>
///     Steam mobile confirmations through the per-account clients of <see cref="AccountSteamClientPool" />. The manual
///     screen goes through the app's <see cref="IInteractiveSessionRunner" /> (dialogs), Nebula Link requests through
///     <see cref="HeadlessSessionPolicy" />.
/// </summary>
public sealed class SteamConfirmationTransport(
    AccountSteamClientPool pool,
    HeadlessSessionPolicy headless,
    IInteractiveSessionRunner? interactive)
    : IConfirmationTransport
{
    public string ProxyKey(MobileDataExtended mafile)
    {
        return pool.Get(mafile).ProxyKey;
    }

    public async Task<IReadOnlyList<Confirmation>> FetchAsync(MobileDataExtended mafile, SessionMode mode, CancellationToken ct)
    {
        var client = pool.Get(mafile);
        if (mode == SessionMode.Interactive)
        {
            if (interactive == null) throw new NotSupportedException("Interactive confirmation reads are not configured");
            return await interactive.RunAsync(() => GetList(mafile, ct), mafile, client.Chp);
        }

        var result = await headless.RunAsync(mafile, client.Chp, client.ProxyConfigured, () => GetList(mafile, ct), ct);
        if (!result.Ok) throw new NebulaCodeException(result.Code, result.Diagnostic);
        return result.Value!;

        async Task<IReadOnlyList<Confirmation>> GetList(MobileDataExtended maf, CancellationToken token)
        {
            var c = pool.Get(maf);
            if (maf.SessionData == null) throw new SessionInvalidException(SessionInvalidException.SESSION_NULL_MSG);
            var items = await SteamMobileConfirmationsApi.GetConfirmations(c.Client, maf, maf.SessionData.SteamId,
                token);
            return items.ToList();
        }
    }

    public async Task<TransportSendResult> SendAsync(MobileDataExtended mafile, IReadOnlyList<Confirmation> items, bool accept,
        SessionMode mode, CancellationToken ct)
    {
        var client = pool.Get(mafile);
        if (mode == SessionMode.Interactive)
        {
            ValidateSession(mafile);
            var ok = await SendWithTradeRetry(mafile, items, accept, ct);
            return new TransportSendResult(ok ? TransportSendStatus.Success : TransportSendStatus.Rejected);
        }

        var result = await headless.RunAsync(mafile, client.Chp, client.ProxyConfigured, async () =>
        {
            try
            {
                return await SendWithTradeRetry(mafile, items, accept, ct);
            }
            catch (Exception ex) when (SteamErrorClassifier.IsBeforeWrite(ex))
            {
                throw new NebulaCodeException(SteamErrorClassifier.NetworkCode(ex, client.ProxyConfigured),
                    NebulaLinkLog.Diagnostic(ex), ex);
            }
            catch (Exception ex) when (ex is not (NebulaCodeException or SessionInvalidException
                                           or SessionPermanentlyExpiredException))
            {
                // The request may have reached Steam
                throw new NebulaCodeException(NebulaResultCodes.ResultUnknown, NebulaLinkLog.Diagnostic(ex), ex);
            }
        }, ct, false);

        if (result.Ok)
            return new TransportSendResult(result.Value ? TransportSendStatus.Success : TransportSendStatus.Rejected);
        return result.Code == NebulaResultCodes.ResultUnknown
            ? new TransportSendResult(TransportSendStatus.Unknown, result.Code, result.Diagnostic)
            : new TransportSendResult(TransportSendStatus.NotSent, result.Code, result.Diagnostic);
    }

    /// <summary>
    ///     A trade send that Steam answers with false is acknowledged and retried once (the first answer was clean, so
    ///     the retry cannot double an accepted send).
    /// </summary>
    private async Task<bool> SendWithTradeRetry(MobileDataExtended mafile, IReadOnlyList<Confirmation> items, bool accept,
        CancellationToken ct)
    {
        var client = pool.Get(mafile);
        var session = mafile.SessionData ?? throw new SessionInvalidException(SessionInvalidException.SESSION_NULL_MSG);
        var ok = await SendOnce();
        if (ok || items.All(c => c.ConfType != ConfirmationType.Trade)) return ok;
        NebulaLinkLog.Warn("Trade confirmation send answered false; sending ack and retrying once");
        await SteamTradeApi.Acknowledge(client.Client, session.SessionId, ct);
        await Task.Delay(10, ct);
        return await SendOnce();

        Task<bool> SendOnce()
        {
            return items.Count == 1
                ? SteamMobileConfirmationsApi.SendConfirmation(client.Client, items[0], session.SteamId, mafile, accept, ct)
                : SteamMobileConfirmationsApi.SendMultipleConfirmations(client.Client, items, session.SteamId, mafile,
                    accept, ct);
        }
    }

    private static void ValidateSession(MobileDataExtended mafile)
    {
        if (mafile.SessionData == null) throw new SessionInvalidException();
        if (mafile.SessionData.RefreshToken.IsExpired) throw new SessionPermanentlyExpiredException();
        var access = mafile.SessionData.GetMobileToken();
        if (access == null || access.Value.IsExpired) throw new SessionPermanentlyExpiredException();
    }
}
