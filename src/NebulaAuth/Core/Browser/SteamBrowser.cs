using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;
using Material.Icons;
using NebulaAuth.Model;
using NebulaAuth.Model.Browser;
using NebulaAuth.Model.Entities;
using NebulaAuth.View.Dialogs;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     "Open in browser": at most one embedded browser session at a time. The same account brings its window to the
///     front; another account asks to close the open window first (cookies are never swapped silently). All members run
///     on the UI thread.
/// </summary>
public static class SteamBrowser
{
    private static readonly TimeSpan ErrorSnackbarDuration = TimeSpan.FromSeconds(10);
    private static BrowserSession? _current;
    private static Task _sweep = Task.CompletedTask;
    private static bool _initialized;

    /// <summary>Engine start in progress: its adapter failure may surface only on the dispatcher (see below).</summary>
    internal static TaskCompletionSource<IPlatformHandle?>? PendingEngineStart { get; set; }

    internal static BrowserSession? Current => _current;

    /// <summary>Most recently created session (also after it closed): diagnostics.</summary>
    internal static BrowserSession? Last { get; private set; }

    /// <summary>
    ///     Called once at startup, before any browser exists: removes profile folders left by a crash (in the background;
    ///     opening waits for it) and routes engine start failures that Avalonia raises on the dispatcher.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        _sweep = Task.Run(() =>
        {
            try
            {
                var removed = BrowserProfileFolder.SweepLeftovers();
                if (removed > 0) Shell.Logger.Info("Removed {count} leftover browser profile folder(s)", removed);
            }
            catch (Exception ex)
            {
                Shell.Logger.Warn(ex, "Leftover browser profile sweep failed");
            }
        });
        // NativeWebView starts the engine in an async void handler: a WebView2 start failure (e.g. COMException
        // 0x80080005) is rethrown on the dispatcher instead of reaching the session. It fails the pending start; the
        // session then tears it down and retries. A start that was already abandoned (timeout, window closed) can
        // still fail later (e.g. E_ABORT when its view was removed): it is only logged, it must not end the app.
        // Unrelated exceptions keep the default handling.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var pending = PendingEngineStart;
            var fromWebView = IsFromWebViewControl(e.Exception);
            if (!fromWebView && (pending == null || e.Exception is not COMException)) return;
            Shell.Logger.Warn(e.Exception, "Browser engine failure raised on the dispatcher");
            pending?.TrySetException(e.Exception);
            e.Handled = true;
        };
    }

    // The embedded browser is the only user of Avalonia.Controls.WebView in the app
    private static bool IsFromWebViewControl(Exception ex)
    {
        var stack = ex.StackTrace;
        return stack != null && (stack.Contains("Avalonia.Controls.Win.WebView2", StringComparison.Ordinal) ||
                                 stack.Contains("Avalonia.Controls.NativeWebView", StringComparison.Ordinal) ||
                                 stack.Contains("Avalonia.Controls.Gtk", StringComparison.Ordinal) ||
                                 stack.Contains("Avalonia.Controls.Macios", StringComparison.Ordinal));
    }

    /// <summary>Opens the browser for <paramref name="mafile" /> (see the class summary for an already open window).</summary>
    public static async Task OpenAsync(Mafile mafile)
    {
        var clickedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Initialize();
        if (_current is {State: BrowserSessionState.Preparing or BrowserSessionState.Ready} current)
        {
            if (ReferenceEquals(current.Account, mafile) ||
                string.Equals(current.AccountName, mafile.AccountName, StringComparison.OrdinalIgnoreCase))
            {
                current.Activate();
                return;
            }

            if (!await ConfirmSwitchAsync(current.AccountName, mafile.AccountName))
            {
                current.Activate();
                return;
            }

            await current.CloseAsync();
        }

        // Only one engine environment at a time: a closing session finishes its cleanup first
        if (_current != null) await _current.Closed;
        if (_current is {State: not BrowserSessionState.Closed}) return;
        await _sweep;

        // Fixed now: the account's proxy, else the default proxy (as every other request of the app)
        var proxyData = mafile.Proxy?.Data ?? MaClient.DefaultProxy;
        var proxy = proxyData == null ? null : UpstreamProxy.FromProxyData(proxyData);
        var session = new BrowserSession(mafile, proxy, clickedAt);
        _current = session;
        Last = session;
        Shell.Logger.Info("Opening browser for {name} (proxy {proxy})", mafile.AccountName, proxy?.ToString() ?? "none");
        await session.RunAsync();
        if (session.Failure is { } failure) ReportFailure(failure);
        _ = session.Closed.ContinueWith(_ =>
        {
            if (ReferenceEquals(_current, session)) _current = null;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static async Task<bool> ConfirmSwitchAsync(string openAccount, string newAccount)
    {
        if (DialogService.IsDialogOpen)
        {
            SnackbarController.SendSnackbar(string.Format(
                LocManager.GetOrDefault("Close the browser of {0} first", "SteamBrowser", "Switch", "CloseFirst"),
                openAccount));
            return false;
        }

        var options = new ConfirmDialogOptions(
            LocManager.GetOrDefault("Browser is open", "SteamBrowser", "Switch", "Title"),
            null,
            MaterialIconKind.Web,
            string.Format(LocManager.GetOrDefault("The browser is open for {0}. Close it and open {1}?",
                "SteamBrowser", "Switch", "Description"), openAccount, newAccount),
            LocManager.GetOrDefault("Only one browser window can be open. Its session ends when it is closed.",
                "SteamBrowser", "Switch", "Hint"),
            ConfirmText: LocManager.GetOrDefault("Close and open", "SteamBrowser", "Switch", "Confirm"));
        return await DialogsController.ShowConfirmCancelDialog(options);
    }

    private static void ReportFailure(BrowserOpenException failure)
    {
        var text = LocManager.GetOrDefault(failure.Error.ToString(), "SteamBrowser", "Errors", failure.Error.ToString());
        if (failure.Error == BrowserError.EngineUnavailable && OperatingSystem.IsWindows())
            text += " " + LocManager.GetOrDefault("", "SteamBrowser", "Errors", "EngineUnavailableWindows");
        if (!string.IsNullOrWhiteSpace(failure.UserDetail)) text += " " + failure.UserDetail;
        TrayManager.RestoreMainWindow();
        SnackbarController.SendSnackbar(text, ErrorSnackbarDuration);
    }

    /// <summary>App exit: the open browser is closed and its profile removed (bounded wait).</summary>
    public static void OnAppExit()
    {
        _current?.ShutdownNow();
        _current = null;
    }
}
