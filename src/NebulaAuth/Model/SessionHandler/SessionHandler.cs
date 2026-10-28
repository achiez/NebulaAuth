using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Utility;
using NebulaAuth.View.Dialogs;
using NebulaAuth.ViewModel.Other;
using SteamLib.Exceptions.Authorization;
using SteamLibForked.Exceptions.Authorization;

namespace NebulaAuth.Model;

public static partial class SessionHandler
{
    private static readonly SemaphoreSlim Semaphore = new(1, 1);

    // Logins (lower case) whose stored Steam password was rejected by Steam in this session
    private static readonly ConcurrentDictionary<string, bool> RejectedStoredPasswords = new();

    /// <summary>
    ///     Steam rejected the stored password of this account in this session: automatic logins with it are skipped (the
    ///     only flag; Nebula Link reports it as invalid_local_password).
    /// </summary>
    public static bool IsStoredPasswordRejected(Mafile mafile)
    {
        return RejectedStoredPasswords.ContainsKey(LoginKey(mafile.AccountName));
    }

    public static void MarkStoredPasswordRejected(Mafile mafile)
    {
        if (RejectedStoredPasswords.TryAdd(LoginKey(mafile.AccountName), true)) NebulaLinkHost.NotifyLocalChange();
    }

    /// <summary>A login succeeded or a new password was saved: automatic logins may use the stored password again.</summary>
    public static void ClearStoredPasswordRejected(string? login)
    {
        if (string.IsNullOrWhiteSpace(login)) return;
        if (RejectedStoredPasswords.TryRemove(LoginKey(login), out _)) NebulaLinkHost.NotifyLocalChange();
    }

    private static string LoginKey(string? login)
    {
        return (login ?? "").Trim().ToLowerInvariant();
    }

    public static async Task<T> Handle<T>(Func<Task<T>> func, Mafile mafile,
        HttpClientHandlerPair? chp = null, string? snackbarPrefix = null, bool allowInteractiveLogin = true)
    {
        chp ??= MaClient.GetHttpClientHandlerPair(mafile);
        await Semaphore.WaitAsync();
        try
        {
            return await HandleInternal(func, chp.Value, mafile, snackbarPrefix, allowInteractiveLogin);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private static async Task<T> HandleInternal<T>(Func<Task<T>> func, HttpClientHandlerPair chp, Mafile mafile,
        string? snackbarPrefix = null, bool allowInteractiveLogin = true)
    {
        using var scope = Shell.Logger.PushScopeProperty("Scope", "SessionHandler");
        Exception? currentException = null;

        if (!mafile.MobileTokenExpired())
        {
            try
            {
                return await func();
            }
            catch (SessionInvalidException ex)
            {
                Shell.Logger.Warn(ex, "Session on mafile {name} {steamid} is invalid when mobile token is not expired",
                    mafile.AccountName, mafile.SessionData?.SteamId);
                currentException = ex;
            }
        }

        if (!mafile.RefreshTokenExpired())
        {
            Shell.Logger.Info("Trying to refresh session on mafile {name} {steamid} using refresh token",
                mafile.AccountName,
                mafile.SessionData?.SteamId);

            var refreshed = await RefreshInternal(chp, mafile);
            if (refreshed)
            {
                SnackbarController.SendSnackbar(snackbarPrefix +
                                                LocManager.GetCodeBehindOrDefault("SessionWasRefreshedAutomatically",
                                                    "SessionHandler", "SessionWasRefreshedAutomatically"));
                try
                {
                    return await func();
                }
                catch (SessionInvalidException ex)
                {
                    Shell.Logger.Warn(ex, "MobileToken on {name} {steamid} was refreshed but after it, error occured",
                        mafile.AccountName, mafile.SessionData?.SteamId);
                    currentException = ex;
                }
            }
        }

        // A stored password Steam already rejected is not tried again until the user sets a new one
        if (!IsStoredPasswordRejected(mafile) && mafile.HasPassword(out var password))
        {
            var logged = await LoginAgainInternal(chp, mafile, password, true, true);
            if (logged)
            {
                Shell.Logger.Debug("Mafile {name} {steamid} was succesfully auto-relogined", mafile.AccountName,
                    mafile.SessionData?.SteamId);
                return await func();
            }
        }

        if (allowInteractiveLogin && !DialogService.IsDialogOpen)
        {
            var noProxy = mafile.Proxy == null && MaClient.DefaultProxy == null;
            var currentPassword = GetPassword(mafile);

            Task<LoginAgainVM?> dialogTask = null!;
            Dispatcher.UIThread.Invoke(() =>
            {
                dialogTask = DialogsController.ShowLoginAgainDialog(mafile.AccountName, currentPassword, noProxy);
            });

            var vm = await dialogTask;
            if (vm != null)
            {
                Shell.Logger.Info("User provided credentials interactively for {name}, attempting login",
                    mafile.AccountName);
                var logged = await LoginAgainInternal(chp, mafile, vm.Password, vm.SavePassword);
                if (logged)
                {
                    Shell.Logger.Debug("Interactive login for {name} succeeded, retrying operation",
                        mafile.AccountName);
                    return await func();
                }
            }
        }

        throw new SessionPermanentlyExpiredException(SessionPermanentlyExpiredException.SESSION_EXPIRED_MSG,
            currentException);
    }

    private static async Task<bool> RefreshInternal(HttpClientHandlerPair chp, Mafile mafile)
    {
        try
        {
            await RefreshMobileToken(chp, mafile);
            return true;
        }
        catch (SessionInvalidException ex)
        {
            Shell.Logger.Debug(ex, "Failed to refresh session on mafile {name} {steamid}", mafile.AccountName,
                mafile.SessionData?.SteamId);
            return false;
        }
    }

    private static async Task<bool> LoginAgainInternal(HttpClientHandlerPair chp, Mafile mafile, string password,
        bool savePassword, bool storedPassword = false)
    {
        var t = Task.Run(OnLoginStarted);
        try
        {
            await LoginAgain(chp, mafile, password, savePassword);
            return true;
        }
        catch (LoginException ex) when (storedPassword && ex.Error == LoginError.InvalidCredentials)
        {
            // Retrying the same stored password on every request would only lock the account: stopped until the user
            // sets a new password (Achiefy sees invalid_local_password for linked accounts)
            Shell.Logger.Warn("Stored Steam password of {name} was rejected; automatic login stopped",
                mafile.AccountName);
            MarkStoredPasswordRejected(mafile);
            SnackbarController.SendSnackbar(LocManager.GetCodeBehindOrDefault(
                "Saved Steam password rejected. Auto-login is off until you set a new one.",
                "SessionHandler", "StoredPasswordRejected"));
            return false;
        }
        catch (Exception ex) //TODO: this will catch any error, even Proxy/Http/Socket errors.
        {
            Shell.Logger.Debug(ex, "Failed to relogin mafile {name} {steamid}", mafile.AccountName,
                mafile.SessionData?.SteamId);
            ExceptionHandler.Handle(ex);
            return false;
        }
        finally
        {
            OnLoginCompleted();
            await t;
        }
    }

    private static async Task OnLoginStarted()
    {
        if (DialogService.IsDialogOpen) return;
        // Completes once the dialog is shown, not when it closes
        await Dispatcher.UIThread.InvokeAsync(() => { _ = DialogService.Show(new WaitLoginDialog()); });
    }

    private static void OnLoginCompleted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var currentSession = DialogService.GetSession();
            if (currentSession is {Content: WaitLoginDialog, IsEnded: false})
            {
                try
                {
                    currentSession.Close();
                }
                catch
                {
                    //Ignored
                }
            }
        });
    }
}