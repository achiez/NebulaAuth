using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Protocol;
using SteamLib;
using SteamLib.Exceptions;
using SteamLib.Exceptions.Authorization;
using SteamLibForked.Exceptions.Authorization;

namespace NebulaAuth.NebulaLink.Sessions;

/// <summary>Result of a session-requiring step of an Achiefy request: <see cref="Code" /> ok, or a protocol result code.</summary>
public readonly record struct HeadlessResult<T>(string Code, T? Value, string? Diagnostic = null)
{
    public bool Ok => Code == NebulaResultCodes.Ok;
}

/// <summary>
///     Seams of <see cref="HeadlessSessionPolicy" /> (Steam calls and local state), replaced by tests.
/// </summary>
public interface IHeadlessSessionBackend
{
    /// <summary>Steam rejected the saved password of the account (the app's flag, the only one).</summary>
    bool IsInvalidPasswordFlagged(MobileDataExtended mafile);
    void SetInvalidPasswordFlag(MobileDataExtended mafile, bool value);
    string? GetStoredPassword(MobileDataExtended mafile);
    Task RefreshAsync(HttpClientHandlerPair chp, MobileDataExtended mafile);
    Task LoginAsync(HttpClientHandlerPair chp, MobileDataExtended mafile, string password);
    void OnSessionRenewed(MobileDataExtended mafile);
}

/// <summary>
///     Mobile session handling for Achiefy requests: no dialogs, no snackbars, a lock per account (not the global lock
///     of the app's interactive session handling). Token valid → run; expired / invalid → refresh; refresh token expired → one
///     re-login with the stored Steam password. A rejected stored password sets the account's invalid-password flag and
///     no automatic login is tried again until the user fixes the password.
/// </summary>
public sealed class HeadlessSessionPolicy
{
    private readonly IHeadlessSessionBackend _backend;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public HeadlessSessionPolicy(IHeadlessSessionBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    ///     Runs <paramref name="func" /> with a valid mobile session. <paramref name="func" /> is called at most twice, and
    ///     a second time only after a <see cref="SessionInvalidException" /> of the first call (a list read, never a send:
    ///     sends pass <paramref name="retryOnInvalidSession" /> = false).
    /// </summary>
    public async Task<HeadlessResult<T>> RunAsync<T>(MobileDataExtended mafile, HttpClientHandlerPair chp, bool proxyConfigured,
        Func<Task<T>> func, CancellationToken ct, bool retryOnInvalidSession = true)
    {
        var gate = _locks.GetOrAdd(AccountSteamClientPool.KeyOf(mafile), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!TokenExpired(mafile))
            {
                try
                {
                    return new HeadlessResult<T>(NebulaResultCodes.Ok, await func());
                }
                catch (SessionInvalidException) when (retryOnInvalidSession)
                {
                    // renewed below
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return Failure<T>(ex, proxyConfigured);
                }
            }

            var renewed = await RenewAsync<T>(mafile, chp, proxyConfigured);
            if (!renewed.Ok) return renewed;
            try
            {
                return new HeadlessResult<T>(NebulaResultCodes.Ok, await func());
            }
            catch (SessionInvalidException ex)
            {
                return new HeadlessResult<T>(NebulaResultCodes.SteamSessionRequired, default, ex.GetType().Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Failure<T>(ex, proxyConfigured);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>check_ready: the account can get a mobile session without the user (no Steam call).</summary>
    public string CheckReady(MobileDataExtended mafile)
    {
        if (_backend.IsInvalidPasswordFlagged(mafile)) return NebulaAccountStates.InvalidLocalPassword;
        var refresh = mafile.SessionData?.RefreshToken;
        if (refresh is {IsExpired: false}) return NebulaAccountStates.Ready;
        return string.IsNullOrEmpty(_backend.GetStoredPassword(mafile))
            ? NebulaAccountStates.SteamSessionRequired
            : NebulaAccountStates.Ready;
    }

    private async Task<HeadlessResult<T>> RenewAsync<T>(MobileDataExtended mafile, HttpClientHandlerPair chp, bool proxyConfigured)
    {
        var refresh = mafile.SessionData?.RefreshToken;
        if (refresh is {IsExpired: false})
        {
            try
            {
                await _backend.RefreshAsync(chp, mafile);
                _backend.OnSessionRenewed(mafile);
                return new HeadlessResult<T>(NebulaResultCodes.Ok, default);
            }
            catch (SessionInvalidException)
            {
                // The refresh token was rejected: continue with the stored password
            }
            catch (Exception ex)
            {
                return Failure<T>(ex, proxyConfigured);
            }
        }

        if (_backend.IsInvalidPasswordFlagged(mafile))
            return new HeadlessResult<T>(NebulaResultCodes.InvalidLocalPassword, default);
        var password = _backend.GetStoredPassword(mafile);
        if (string.IsNullOrEmpty(password))
            return new HeadlessResult<T>(NebulaResultCodes.SteamSessionRequired, default);

        try
        {
            await _backend.LoginAsync(chp, mafile, password);
            _backend.OnSessionRenewed(mafile);
            return new HeadlessResult<T>(NebulaResultCodes.Ok, default);
        }
        catch (LoginException ex) when (ex.Error == LoginError.InvalidCredentials)
        {
            _backend.SetInvalidPasswordFlag(mafile, true);
            return new HeadlessResult<T>(NebulaResultCodes.InvalidLocalPassword, default, "InvalidCredentials");
        }
        catch (LoginException ex)
        {
            return new HeadlessResult<T>(NebulaResultCodes.SteamRejected, default, "Login " + ex.Error);
        }
        catch (Exception ex)
        {
            return Failure<T>(ex, proxyConfigured);
        }
    }

    private static bool TokenExpired(MobileDataExtended mafile)
    {
        var token = mafile.SessionData?.GetMobileToken();
        return token == null || token.Value.IsExpired;
    }

    private static HeadlessResult<T> Failure<T>(Exception ex, bool proxyConfigured)
    {
        var code = ex switch
        {
            NebulaCodeException nce => nce.Code,
            SessionInvalidException or SessionPermanentlyExpiredException => NebulaResultCodes.SteamSessionRequired,
            CantLoadConfirmationsException {Error: LoadConfirmationsError.TryAgainLater} => NebulaResultCodes.RateLimited,
            CantLoadConfirmationsException => NebulaResultCodes.SteamUnavailable,
            HttpRequestException => SteamErrorClassifier.NetworkCode(ex, proxyConfigured),
            TaskCanceledException => NebulaResultCodes.SteamUnavailable,
            _ => NebulaResultCodes.InternalError
        };
        return new HeadlessResult<T>(code, default, NebulaLinkLog.Diagnostic(ex));
    }
}
