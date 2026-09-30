using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DialogHostAvalonia;
using NebulaAuth.Model;
using NebulaAuth.Model.Browser;
using NebulaAuth.Model.Entities;
using NebulaAuth.View;

namespace NebulaAuth.Core.Browser;

public enum BrowserSessionState
{
    Preparing,
    Ready,
    Closing,
    Closed
}

/// <summary>
///     One embedded browser window of one account, from the click to the cleanup:
///     <list type="number">
///         <item>the mafile and its effective proxy are fixed at the click;</item>
///         <item>the tunnel to Steam Community is checked through the local forwarder (proxy errors before any page);</item>
///         <item>the session is validated / renewed (<see cref="SteamWebSession" />);</item>
///         <item>a fresh engine environment is started (one retry with a fresh profile after a start failure);</item>
///         <item>only the account's cookies are added, Steam Community is loaded and must show the account's Steam ID;</item>
///         <item>only then the page is revealed.</item>
///     </list>
///     Closing (or any failure) releases the engine, waits for its processes, stops the forwarder and deletes the profile
///     folder. All members run on the UI thread.
/// </summary>
internal sealed partial class BrowserSession
{
    private static readonly Uri CommunityHome = new("https://steamcommunity.com/");
    private static readonly TimeSpan EngineStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailedExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     JSON text: host, path and <c>g_steamID</c> of the page (false when signed out); <c>defined</c> = the page's inline
    ///     script that sets <c>g_steamID</c> already ran.
    /// </summary>
    private const string VERIFY_SCRIPT =
        "(function(){try{var d=(typeof g_steamID!=='undefined');var id=d?g_steamID:null;" +
        "return JSON.stringify({host:location.hostname,path:location.pathname,defined:d,steamId:(id===false||id===null)?null:String(id)});}" +
        "catch(e){return JSON.stringify({error:String(e)});}})()";


    private static readonly TimeSpan EarlyVerifyPoll = TimeSpan.FromMilliseconds(100);

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly UpstreamProxy? _proxy;
    private BrowserEngine? _engine;
    private LocalForwarder? _forwarder;
    private BrowserProfileFolder? _profile;
    private NativeWebView? _view;
    private SteamBrowserWindow? _window;
    private bool _windowClosed;

    private readonly long _startedAt;

    /// <param name="startedAt"><see cref="Stopwatch.GetTimestamp" /> of the click (stage timing log).</param>
    public BrowserSession(Mafile account, UpstreamProxy? proxy, long startedAt)
    {
        Account = account;
        AccountName = account.AccountName;
        _proxy = proxy;
        _startedAt = startedAt;
    }

    /// <summary>Stage durations of the last open (log / debug harness): proxy, session, engine, verify, total (ms).</summary>
    internal Dictionary<string, long> StageTimings { get; } = [];

    private long MarkStage(string stage, long since)
    {
        var now = Stopwatch.GetTimestamp();
        StageTimings[stage] = (long) Stopwatch.GetElapsedTime(since, now).TotalMilliseconds;
        return now;
    }

    public Mafile Account { get; }
    public string AccountName { get; }
    public BrowserSessionState State { get; private set; } = BrowserSessionState.Preparing;

    /// <summary>Completes when the session is closed and cleaned up (also after a failure).</summary>
    public Task Closed => _closed.Task;

    /// <summary>Set when opening failed (null after a normal close or a cancel).</summary>
    public BrowserOpenException? Failure { get; private set; }

    public void Activate()
    {
        if (_window == null || _windowClosed) return;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            _window = new SteamBrowserWindow();
            _window.SetAccount(AccountName);
            _window.Closed += (_, _) => OnWindowClosed();
            _window.NavigateBackRequested += () => _engine?.GoBack();
            _window.NavigateForwardRequested += () => _engine?.GoForward();
            _window.NavigateReloadRequested += () => _engine?.Reload();
            SetStatus("Proxy", "Checking the proxy…");
            _window.Show();

            _engine = BrowserEngine.Create();
            _engine.EnsureSupported(_proxy != null);
            var stage = Stopwatch.GetTimestamp();
            await CheckTunnelAsync(ct);
            stage = MarkStage("proxy", stage);

            SetStatus("Session", "Checking the Steam session…");
            var session = await PrepareSessionAsync(ct);
            stage = MarkStage("session", stage);
            var expectedSteamId = session.SteamId.Steam64.ToUlong();
            DebugAdjustExpectedSteamId(ref expectedSteamId);
            var language = LocManager.GetCurrentLanguageCode();
            var cookies = SteamWebSession.BuildCookies(session, SteamWebSession.SteamLanguage(language));

            SetStatus("Engine", "Starting the browser…");
            await StartEngineAsync(SteamWebSession.EngineLanguage(language), ct);
            stage = MarkStage("engine", stage);

            SetStatus("Verify", "Signing in to Steam Community…");
            await _engine.SetCookiesAsync(cookies);
            await LoadAndVerifyAccountAsync(expectedSteamId, ct);
            ct.ThrowIfCancellationRequested();

            _engine.NavigationStateChanged += UpdateNavigationState;
            _engine.Blocked += ShowBlockedNotice;
            _window.RevealWebView();
            MarkStage("verify", stage);
            MarkStage("total", _startedAt);
            UpdateNavigationState();
            State = BrowserSessionState.Ready;
            // Durations only (no account data beyond the login name)
            Shell.Logger.Info("Browser for {name}: stage timings (ms) proxy={proxy} session={session} engine={engine} " +
                              "verify={verify} (early {early}) total={total}", AccountName, StageTimings["proxy"],
                StageTimings["session"], StageTimings["engine"], StageTimings["verify"],
                StageTimings.GetValueOrDefault("verifiedEarly") == 1, StageTimings["total"]);
            Shell.Logger.Info("Browser for {name} opened ({engine}, proxy {proxy})", AccountName, _engine.EngineName,
                _proxy?.ToString() ?? "none");
        }
        catch (Exception ex)
        {
            var failure = ex switch
            {
                _ when ct.IsCancellationRequested => null,
                BrowserOpenException {Error: BrowserError.Cancelled} => null,
                BrowserOpenException boe => boe,
                _ => new BrowserOpenException(BrowserError.EngineUnavailable, ex.Message, ex)
            };
            if (failure != null)
            {
                Failure = failure;
                Shell.Logger.Warn(failure.InnerException, "Browser for {name} not opened: {error} ({detail})",
                    AccountName, failure.Error, failure.Message);
            }
            else
            {
                Shell.Logger.Info("Browser for {name}: opening cancelled", AccountName);
            }

            await CloseAsync();
        }
    }

    private async Task CheckTunnelAsync(CancellationToken ct)
    {
        if (_proxy == null) return;
        _forwarder = LocalForwarder.Start(_proxy, _engine!.ForwarderNeedsCredentials);
        UpstreamFailure? failure;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            failure = await _forwarder.ProbeAsync("steamcommunity.com", 443, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            failure = UpstreamFailure.Timeout;
        }

        if (failure is { } f)
            throw new BrowserOpenException(BrowserOpenException.FromUpstream(f), "Proxy tunnel check failed: " + f);
    }

    /// <summary>
    ///     Session check / renewal. A sign-in dialog shown meanwhile is in the main window: it is brought to the front and
    ///     the loading text points there.
    /// </summary>
    private async Task<SteamLibForked.Models.Session.MobileSessionData> PrepareSessionAsync(CancellationToken ct)
    {
        var host = DialogService.Host;

        void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != DialogHost.IsOpenProperty || State != BrowserSessionState.Preparing) return;
            if (e.NewValue is true)
            {
                SetStatus("SignInInMain", "Continue in the NebulaAuth window");
                TrayManager.RestoreMainWindow();
            }
            else
            {
                SetStatus("Session", "Checking the Steam session…");
            }
        }

        if (host != null) host.PropertyChanged += OnHostPropertyChanged;
        try
        {
            var session = await SteamWebSession.EnsureSessionAsync(Account, _proxy);
            ct.ThrowIfCancellationRequested();
            return session;
        }
        finally
        {
            if (host != null) host.PropertyChanged -= OnHostPropertyChanged;
        }
    }

    /// <summary>
    ///     Starts the engine; after a start failure everything of the attempt is torn down (our engine processes, the
    ///     profile folder) and it is retried once with a fresh profile and without GPU.
    /// </summary>
    private async Task StartEngineAsync(string language, CancellationToken ct)
    {
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await StartEngineAttemptAsync(language, attempt, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt == 1 &&
                                       ex is not BrowserOpenException
                                       {
                                           Error: BrowserError.ProxyNotSupported or BrowserError.ProfilePathTooLong
                                       })
            {
                Shell.Logger.Warn(ex, "Browser engine start failed (attempt 1), retrying with a fresh profile");
                await TearDownEngineAsync(killLeftovers: true);
                _engine = BrowserEngine.Create();
            }
            catch (Exception ex) when (ex is not BrowserOpenException && !ct.IsCancellationRequested)
            {
                throw new BrowserOpenException(BrowserError.EngineUnavailable, "Browser engine start failed", ex);
            }
        }
    }

    private async Task StartEngineAttemptAsync(string language, int attempt, CancellationToken ct)
    {
        var engine = _engine!;
        if (engine.NeedsProfileFolder)
        {
            try
            {
                _profile = BrowserProfileFolder.Create();
            }
            catch (System.IO.PathTooLongException ex)
            {
                throw new BrowserOpenException(BrowserError.ProfilePathTooLong, ex.Message, ex);
            }
        }

        var options = new EngineOptions
        {
            ProxyServer = _forwarder?.ProxyServer,
            ProxyUser = _forwarder?.LocalUser,
            ProxyPassword = _forwarder?.LocalPassword,
            ProxyPort = _forwarder?.Port ?? 0,
            Language = language,
            Profile = _profile,
            Attempt = attempt
        };

        var adapter = new TaskCompletionSource<IPlatformHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Opaque white page background like any browser (NativeWebView defaults to transparent, which shows the dark
        // window through pages without their own background)
        var view = new NativeWebView {Background = Avalonia.Media.Brushes.White};
        view.EnvironmentRequested += (_, e) =>
        {
            try
            {
                engine.ConfigureEnvironment(e, options);
            }
            catch (Exception ex)
            {
                adapter.TrySetException(ex);
            }
        };
        view.AdapterCreated += (_, e) => adapter.TrySetResult(e.TryGetPlatformHandle());
        _view = view;
        SteamBrowser.PendingEngineStart = adapter;
        try
        {
            _window!.AttachWebView(view);
            var startTimeout = EngineStartTimeout;
            DebugAdjustStartTimeout(ref startTimeout);
            var handle = await adapter.Task.WaitAsync(startTimeout, ct);
            await engine.AttachAsync(view, handle, options);
            DebugSimulateStartFailure(attempt);
        }
        catch (TimeoutException ex)
        {
            throw new BrowserOpenException(BrowserError.EngineUnavailable, "Browser engine start timed out", ex);
        }
        finally
        {
            SteamBrowser.PendingEngineStart = null;
        }
    }

    /// <summary>
    ///     Loads Steam Community and checks that it shows <paramref name="expectedSteamId" />. The check runs as soon as the
    ///     HTML is parsed (<c>g_steamID</c> is set by an inline script of the page), so the page is revealed while images and
    ///     scripts still load. Only a match is decided early: anything else (not defined yet, another / no account, an error
    ///     page) waits for the full load, whose failure is reported as before.
    /// </summary>
    private async Task LoadAndVerifyAccountAsync(ulong expectedSteamId, CancellationToken ct)
    {
        var engine = _engine!;
        var navigation = await engine.NavigateToDocumentAsync(CommunityHome, NavigationTimeout, ct);
        ct.ThrowIfCancellationRequested();
        ThrowIfLoadFailed(navigation);
        if (navigation.DocumentReady)
        {
            // Polled until the page matches or its load ends. The first document can also be the page of the local
            // proxy's credential challenge, which WebView2 replaces by the real one right after: it never matches.
            var load = engine.DocumentLoad!;
            var sw = Stopwatch.StartNew();
            while (!load.IsCompleted && sw.Elapsed < NavigationTimeout)
            {
                var page = await ReadAccountAsync();
                ct.ThrowIfCancellationRequested();
                if (IsExpectedAccount(page, expectedSteamId))
                {
                    StageTimings["verifiedEarly"] = 1;
                    return;
                }

                // Another account or signed out: decided on the full load (an error page is reported as such)
                if (page.Defined) break;
                await Task.WhenAny(load, Task.Delay(EarlyVerifyPoll, ct));
            }

            navigation = await engine.WaitForLoadAsync(NavigationTimeout, ct);
            ct.ThrowIfCancellationRequested();
            ThrowIfLoadFailed(navigation);
        }

        StageTimings["verifiedEarly"] = 0;
        var final = await ReadAccountAsync();
        if (!IsExpectedAccount(final, expectedSteamId))
        {
            // Steam IDs only (no tokens): which account the page sees, if any
            throw new BrowserOpenException(BrowserError.SessionRejected,
                $"Steam Community shows {(final.SteamId ?? "no account")} at {final.Host}{final.Path}, expected {expectedSteamId}");
        }
    }

    private void ThrowIfLoadFailed(NavigationResult navigation)
    {
        if (navigation.IsSuccess) return;
        if (_forwarder?.LastFailure is { } upstream)
            throw new BrowserOpenException(BrowserOpenException.FromUpstream(upstream),
                "Steam Community load failed at the proxy: " + upstream);
        throw new BrowserOpenException(BrowserError.SteamUnreachable,
            "Steam Community load failed: " + navigation.Error);
    }

    private sealed record PageAccount(string? Host, string? Path, bool Defined, string? SteamId);

    private static bool IsExpectedAccount(PageAccount page, ulong expectedSteamId)
    {
        var onCommunity = string.Equals(page.Host, CommunityHome.Host, StringComparison.OrdinalIgnoreCase) &&
                          page.Path != null && !page.Path.StartsWith("/login", StringComparison.OrdinalIgnoreCase);
        return onCommunity && page.SteamId == expectedSteamId.ToString();
    }

    private async Task<PageAccount> ReadAccountAsync()
    {
        var raw = await _engine!.ExecuteScriptAsync(VERIFY_SCRIPT);
        try
        {
            var json = raw;
            // WebView2 returns the script result as JSON (a JSON string here); other engines return the string itself
            if (json != null && json.StartsWith('"')) json = JsonSerializer.Deserialize<string>(json);
            if (json == null) return new PageAccount(null, null, false, null);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new PageAccount(
                root.TryGetProperty("host", out var h) ? h.GetString() : null,
                root.TryGetProperty("path", out var p) ? p.GetString() : null,
                root.TryGetProperty("defined", out var d) && d.ValueKind == JsonValueKind.True,
                root.TryGetProperty("steamId", out var s) ? s.GetString() : null);
        }
        catch (JsonException)
        {
            // treated as not signed in
            return new PageAccount(null, null, false, null);
        }
    }
    private void SetStatus(string key, string fallback)
    {
        if (_window == null) return;
        var title = string.Format(LocManager.GetOrDefault("Opening Steam for {0}", "SteamBrowser", "Loading", "Title"),
            AccountName);
        _window.SetLoading(title, LocManager.GetOrDefault(fallback, "SteamBrowser", "Loading", key));
    }

    private void UpdateNavigationState()
    {
        if (_engine == null || _window == null || _windowClosed) return;
        _window.SetNavigationState(_engine.CurrentUrl, _engine.CanGoBack, _engine.CanGoForward);
    }

    private void ShowBlockedNotice(string key)
    {
        if (_window == null || _windowClosed) return;
        _window.ShowNotice(LocManager.GetOrDefault(key, "SteamBrowser", "Notices", key));
    }

    private void OnWindowClosed()
    {
        _windowClosed = true;
        _ = CloseAsync();
    }

    /// <summary>Closes the window and cleans up everything of this session. Safe to call more than once.</summary>
    public Task CloseAsync()
    {
        if (State is BrowserSessionState.Closing or BrowserSessionState.Closed) return Closed;
        State = BrowserSessionState.Closing;
        _cts.Cancel();
        return CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (!_windowClosed)
            {
                _windowClosed = true;
                _window?.Close();
            }

            await TearDownEngineAsync(killLeftovers: true);
            if (_forwarder != null)
            {
                await _forwarder.DisposeAsync();
                _forwarder = null;
            }
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Browser cleanup failed");
        }
        finally
        {
            State = BrowserSessionState.Closed;
            _closed.TrySetResult();
        }
    }

    /// <summary>
    ///     Releases the engine, removes the view (the native engine closes with it), waits for the engine processes and
    ///     deletes the profile folder.
    /// </summary>
    private async Task TearDownEngineAsync(bool killLeftovers)
    {
        var engine = _engine;
        engine?.Release();
        _window?.DetachWebView();
        _view = null;
        if (engine != null)
        {
            var failed = State == BrowserSessionState.Preparing || Failure != null;
            var report = await engine.WaitForExitAsync(failed ? FailedExitTimeout : ExitTimeout, killLeftovers);
            LastExitReport = report;
            if (report.StillAlive.Count > 0 || report.Killed.Count > 0)
                Shell.Logger.Warn("Browser engine processes: {alive} still alive, {killed} terminated",
                    report.StillAlive.Count, report.Killed.Count);
        }

        if (_profile != null)
        {
            var profile = _profile;
            _profile = null;
            LastProfileDeleted = await profile.DeleteAsync(DeleteTimeout);
            LastProfilePath = profile.Path;
        }
    }

    /// <summary>App exit: bounded synchronous cleanup (a leftover folder is removed by the next start).</summary>
    public void ShutdownNow()
    {
        if (State == BrowserSessionState.Closed) return;
        State = BrowserSessionState.Closing;
        _cts.Cancel();
        try
        {
            _engine?.Release();
            if (!_windowClosed)
            {
                _windowClosed = true;
                _window?.Close();
            }

            _window?.DetachWebView();
            // The app is exiting: the engine gets a short grace period, then its processes are terminated, so the
            // profile folder is unlocked and can be deleted now
            _engine?.WaitForExitNow(TimeSpan.FromSeconds(2), killLeftovers: true);
            if (_profile?.DeleteNow(TimeSpan.FromSeconds(5)) == false)
                Shell.Logger.Warn("Browser profile folder not deleted on exit; the next start removes it");
            _ = _forwarder?.DisposeAsync();
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "Browser cleanup on exit failed");
        }
        finally
        {
            State = BrowserSessionState.Closed;
            _closed.TrySetResult();
        }
    }

    /// <summary>Debug builds: simulated engine start failure (BrowserSession.Debug.cs).</summary>
    partial void DebugSimulateStartFailure(int attempt);

    /// <summary>Debug builds: another expected account, as if Steam showed a different one (BrowserSession.Debug.cs).</summary>
    static partial void DebugAdjustExpectedSteamId(ref ulong steamId);

    /// <summary>Debug builds: shorter engine start timeout (BrowserSession.Debug.cs).</summary>
    static partial void DebugAdjustStartTimeout(ref TimeSpan timeout);

    // Diagnostics of the last cleanup (log / debug harness)
    internal EngineExitReport? LastExitReport { get; private set; }
    internal bool? LastProfileDeleted { get; private set; }
    internal string? LastProfilePath { get; private set; }
    internal BrowserEngine? Engine => _engine;
    internal LocalForwarder? Forwarder => _forwarder;
    internal string? ProfilePath => _profile?.Path;
    internal SteamBrowserWindow? Window => _window;
}
