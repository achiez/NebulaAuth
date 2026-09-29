using System;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using NebulaAuth.Utility;
using NebulaAuth.View.Dialogs;
using NebulaAuth.ViewModel.Other;
using SteamLib.Exceptions.Authorization;

namespace NebulaAuth.Model;

public static partial class SessionHandler
{
    private static readonly SemaphoreSlim Semaphore = new(1, 1);

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

        if (mafile.HasPassword(out var password))
        {
            var logged = await LoginAgainInternal(chp, mafile, password, true);
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
        bool savePassword)
    {
        var t = Task.Run(OnLoginStarted);
        try
        {
            await LoginAgain(chp, mafile, password, savePassword);
            return true;
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