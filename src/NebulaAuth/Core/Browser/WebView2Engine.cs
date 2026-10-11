using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Browser;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     Windows: WebView2 through Avalonia <see cref="NativeWebView" />, with the managed <see cref="CoreWebView2" /> over
///     its native handle for what Avalonia does not expose (proxy authentication, cookie attributes, UA client hints,
///     downloads, permissions, process ids).
///     <list type="bullet">
///         <item>Fresh InPrivate profile in a short temp folder (<see cref="BrowserProfileFolder" />) for every open.</item>
///         <item>
///             <c>--proxy-server</c> points to the session's local forwarder (loopback not bypassed); its random
///             credentials are answered in <c>BasicAuthenticationRequested</c> only for the forwarder realm.
///         </item>
///         <item>
///             WebRTC may not use UDP outside the proxy; WAM/OneAuth (direct connection to Microsoft), SmartScreen,
///             background networking, component updates are off.
///         </item>
///     </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WebView2Engine : BrowserEngine
{
    /// <summary>Navigation failures of the forwarder challenge before WebView2 retries with the credentials.</summary>
    private const int MAX_PROXY_AUTH_RETRIES = 3;

    private static readonly TimeSpan AuthRetryGrace = TimeSpan.FromSeconds(3);

    private readonly TaskCompletionSource _browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> _pids = [];
    private CoreWebView2? _core;
    private CoreWebView2Environment? _environment;
    private string? _localPassword;
    private string? _localUser;
    private PendingNavigation? _navigation;
    private PendingNavigation? _documentNavigation;
    private int _navigationStarts;

    public override bool NeedsProfileFolder => true;
    public override bool ForwarderNeedsCredentials => true;
    public override string EngineName => "WebView2 " + (_environment?.BrowserVersionString ?? "?");
    public override string? CurrentUrl => _core?.Source;
    public override bool CanGoBack => _core?.CanGoBack == true;
    public override bool CanGoForward => _core?.CanGoForward == true;

    /// <summary>Chrome UA of the engine's Chromium major version (set before the first navigation).</summary>
    public string? UserAgent { get; private set; }

    public override void ConfigureEnvironment(WebViewEnvironmentRequestedEventArgs args, EngineOptions options)
    {
        if (args is not WindowsWebView2EnvironmentRequestedEventArgs w)
            throw new BrowserOpenException(BrowserError.EngineUnavailable,
                "Expected the WebView2 engine, got " + args.GetType().Name);

        var arguments = new List<string>
        {
            // WebRTC: no UDP outside the proxy (no local / public IP disclosure)
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
            "--webrtc-ip-handling-policy=disable_non_proxied_udp",
            // msOneAuthWAM: without it the browser process connects DIRECTLY to Microsoft (OS account profile);
            // SmartScreen would send the visited URLs to Microsoft outside the proxy
            "--disable-features=msOneAuthWAM,msSmartScreenProtection",
            "--no-first-run",
            "--disable-background-networking",
            "--disable-domain-reliability",
            "--disable-component-update"
        };
        if (options.ProxyServer != null)
        {
            arguments.Add("--proxy-server=" + options.ProxyServer);
            // Chromium bypasses loopback by default: "<-loopback>" removes that rule, so nothing goes direct
            arguments.Add("--proxy-bypass-list=<-loopback>");
            _localUser = options.ProxyUser;
            _localPassword = options.ProxyPassword;
        }

        if (options.Attempt > 1) arguments.Add("--disable-gpu");

        w.UserDataFolder = options.Profile!.Path;
        w.AdditionalBrowserArguments = string.Join(' ', arguments);
        w.IsInPrivateModeEnabled = true;
        w.Language = options.Language;
        w.AllowSingleSignOnUsingOSPrimaryAccount = false;
        w.EnableDevTools = false;
        DebugConfigureEnvironment(w);
    }

    /// <summary>Debug builds: broken engine location.</summary>
    partial void DebugConfigureEnvironment(WindowsWebView2EnvironmentRequestedEventArgs args);

    public override async Task AttachAsync(NativeWebView view, IPlatformHandle? handle, EngineOptions options)
    {
        if (handle is not IWindowsWebView2PlatformHandle wh)
            throw new BrowserOpenException(BrowserError.EngineUnavailable, "No WebView2 handle");
        var ptr = wh.CoreWebView2;
        if (ptr == IntPtr.Zero) throw new BrowserOpenException(BrowserError.EngineUnavailable, "No CoreWebView2");
        try
        {
            _core = CoreWebView2.CreateFromComICoreWebView2(ptr);
        }
        finally
        {
            // The Avalonia getter returned an AddRef'ed pointer; the managed wrapper holds its own reference
            Marshal.Release(ptr);
        }

        _environment = _core.Environment;
        var core = _core;

        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreHostObjectsAllowed = false;
        try
        {
            // SmartScreen (reputation checks of visited URLs by Microsoft, outside the proxy)
            settings.IsReputationCheckingRequired = false;
        }
        catch (NotImplementedException)
        {
            // older runtime: also disabled by the msSmartScreenProtection feature switch
        }

        core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.DOMContentLoaded += OnDomContentLoaded;
        core.SourceChanged += (_, _) => RaiseNavigationStateChanged();
        core.HistoryChanged += (_, _) => RaiseNavigationStateChanged();
        // Links opening a new window (target=_blank, window.open) stay in this view: same environment and cookies
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            RaiseBlocked("DownloadBlocked");
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.ServerCertificateErrorDetected += (_, e) =>
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        // Sign-out requested by a script (fetch / XHR) is answered with 403 like a blocked navigation
        core.AddWebResourceRequestedFilter("*logout*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        _environment.BrowserProcessExited += (_, _) => _browserExited.TrySetResult();
        _environment.ProcessInfosChanged += (_, _) => TrackProcesses();
        TrackProcesses();

        await ApplyUserAgentAsync(core);
    }

    /// <summary>
    ///     A Chrome UA of the engine's Chromium version instead of the Edge WebView2 one, with matching client hints
    ///     (Sec-CH-UA headers, navigator.userAgentData): Google Chrome / Chromium brands, no Edge / WebView2.
    /// </summary>
    private async Task ApplyUserAgentAsync(CoreWebView2 core)
    {
        var major = ParseMajor(_environment?.BrowserVersionString) ?? "140";
        var ua =
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
        UserAgent = ua;
        core.Settings.UserAgent = ua;
        var fullVersion = major + ".0.0.0";
        // Windows 11 reports platform version 13+ in client hints, Windows 10 reports 10.0.0
        var platformVersion = Environment.OSVersion.Version.Build >= 22000 ? "15.0.0" : "10.0.0";
        var metadata = new
        {
            userAgent = ua,
            platform = "Win32",
            userAgentMetadata = new
            {
                brands = new[]
                {
                    new {brand = "Google Chrome", version = major},
                    new {brand = "Chromium", version = major},
                    new {brand = "Not?A_Brand", version = "99"}
                },
                fullVersionList = new[]
                {
                    new {brand = "Google Chrome", version = fullVersion},
                    new {brand = "Chromium", version = fullVersion},
                    new {brand = "Not?A_Brand", version = "99.0.0.0"}
                },
                fullVersion,
                platform = "Windows",
                platformVersion,
                architecture = "x86",
                model = "",
                mobile = false,
                bitness = "64",
                wow64 = false
            }
        };
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",
                JsonSerializer.Serialize(metadata));
        }
        catch (Exception ex)
        {
            // The UA header stays the Chrome one (Settings.UserAgent); only the client hints keep the Edge brand
            Shell.Logger.Warn(ex, "Browser: client hints override failed");
        }
    }

    private static string? ParseMajor(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        var digits = new string(version.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    private void TrackProcesses()
    {
        var core = _core;
        var env = _environment;
        if (core == null || env == null) return;
        try
        {
            lock (_pids)
            {
                _pids.Add((int) core.BrowserProcessId);
                foreach (var info in env.GetProcessInfos()) _pids.Add(info.ProcessId);
            }
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidOperationException)
        {
            // closing
        }
    }

    private void OnBasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        // Only the local forwarder's challenge gets its (random, per-open) credentials. A site asking for credentials
        // is cancelled: nothing is sent to it.
        if (_localUser != null && e.Challenge.Contains($"realm=\"{LocalForwarder.REALM}\"", StringComparison.Ordinal))
        {
            e.Response.UserName = _localUser;
            e.Response.Password = _localPassword;
        }
        else
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Interlocked.Increment(ref _navigationStarts);
        // The first navigation after Navigate() (or its restart after the proxy challenge) is the awaited one
        if (_navigation is {NavigationId: null} pending) pending.NavigationId = e.NavigationId;
        Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri);
        switch (NavigationPolicy.Check(uri))
        {
            case NavigationDecision.BlockScheme:
                e.Cancel = true;
                break;
            case NavigationDecision.BlockSignOut:
                e.Cancel = true;
                RaiseBlocked("SignOutBlocked");
                break;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && NavigationPolicy.Check(uri) == NavigationDecision.Allow)
            _core?.Navigate(uri.AbsoluteUri);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !NavigationPolicy.IsSteamSignOut(uri)) return;
        if (_environment == null) return;
        e.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        RaiseBlocked("SignOutBlocked");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        RaiseNavigationStateChanged();
        var pending = _navigation;
        // Completion of an older navigation (e.g. a cancelled one) is not the awaited one
        if (pending == null || (pending.NavigationId != null && pending.NavigationId != e.NavigationId)) return;
        if (!e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired &&
            _localUser != null && pending.ProxyAuthRetries < MAX_PROXY_AUTH_RETRIES)
        {
            // The first request gets the forwarder's 407; WebView2 asks BasicAuthenticationRequested and restarts the
            // navigation itself. Re-navigate only if it did not.
            pending.ProxyAuthRetries++;
            pending.NavigationId = null;
            var startsBefore = Volatile.Read(ref _navigationStarts);
            _ = RetryIfNotRestartedAsync(pending, startsBefore);
            return;
        }

        pending.Complete(new NavigationResult(e.IsSuccess, e.IsSuccess ? null : e.WebErrorStatus.ToString()));
        if (_navigation == pending) _navigation = null;
    }

    private void OnDomContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        // Only the HTML of the awaited navigation (after a proxy challenge restart its id is taken again on start)
        if (_navigation is {NavigationId: { } id} pending && id == e.NavigationId) pending.SetDocumentReady();
    }

    private async Task RetryIfNotRestartedAsync(PendingNavigation pending, int startsBefore)
    {
        await Task.Delay(AuthRetryGrace);
        if (pending.IsCompleted || _navigation != pending || Volatile.Read(ref _navigationStarts) != startsBefore) return;
        _core?.Navigate(pending.Url.AbsoluteUri);
    }

    public override async Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies)
    {
        var manager = _core?.CookieManager ?? throw new InvalidOperationException("Engine not attached");
        manager.DeleteAllCookies();
        foreach (var c in cookies)
        {
            var cookie = manager.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
            cookie.IsSecure = c.Secure;
            cookie.IsHttpOnly = c.HttpOnly;
            cookie.SameSite = CoreWebView2CookieSameSiteKind.None;
            if (c.Expires is { } expires) cookie.Expires = expires.UtcDateTime;
            manager.AddOrUpdateCookie(cookie);
        }

        // Every cookie must be in the store before the first request
        var stored = await manager.GetCookiesAsync(null);
        if (stored.Count < cookies.Count)
            throw new BrowserOpenException(BrowserError.EngineUnavailable,
                $"Cookie store has {stored.Count} of {cookies.Count} cookies");
    }

    public override async Task<NavigationResult> NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        var core = _core ?? throw new InvalidOperationException("Engine not attached");
        var pending = new PendingNavigation(url);
        _navigation = pending;
        try
        {
            core.Navigate(url.AbsoluteUri);
            return await pending.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            _core?.Stop();
            return new NavigationResult(false, "Timeout");
        }
        finally
        {
            if (_navigation == pending) _navigation = null;
        }
    }

    public override async Task<NavigationResult> NavigateToDocumentAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        var core = _core ?? throw new InvalidOperationException("Engine not attached");
        var pending = new PendingNavigation(url);
        _navigation = pending;
        _documentNavigation = pending;
        try
        {
            core.Navigate(url.AbsoluteUri);
            // A failed navigation completes without (or before) its document: its failure is returned right away
            var first = await Task.WhenAny(pending.DocumentReady, pending.Task).WaitAsync(timeout, ct);
            return first == pending.Task ? pending.Task.Result : new NavigationResult(true, null, DocumentReady: true);
        }
        catch (TimeoutException)
        {
            _core?.Stop();
            return new NavigationResult(false, "Timeout");
        }
    }

    public override Task<NavigationResult>? DocumentLoad => _documentNavigation?.Task;

    public override async Task<NavigationResult> WaitForLoadAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pending = _documentNavigation ?? throw new InvalidOperationException("No navigation");
        try
        {
            return await pending.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            _core?.Stop();
            return new NavigationResult(false, "Timeout");
        }
    }

    public override async Task<string?> ExecuteScriptAsync(string script)
    {
        var core = _core;
        if (core == null) return null;
        return await core.ExecuteScriptAsync(script);
    }

    public override void GoBack()
    {
        if (_core?.CanGoBack == true) _core.GoBack();
    }

    public override void GoForward()
    {
        if (_core?.CanGoForward == true) _core.GoForward();
    }

    public override void Reload()
    {
        _core?.Reload();
    }

    public override void Release()
    {
        TrackProcesses();
        _navigation?.Complete(new NavigationResult(false, "Closed"));
        _documentNavigation?.Complete(new NavigationResult(false, "Closed"));
        _core = null;
        _environment = null;
    }

    public override async Task<EngineExitReport> WaitForExitAsync(TimeSpan timeout, bool killLeftovers)
    {
        var report = new EngineExitReport();
        var sw = Stopwatch.StartNew();
        var pids = SnapshotPids();
        report.Processes.AddRange(pids);
        // The engine closes when the last reference to the controller / environment is released
        CollectReleasedWrappers();
        while (sw.Elapsed < timeout)
        {
            if (_browserExited.Task.IsCompleted) report.ExitedEvent = true;
            if (report.ExitedEvent && !WindowsProcesses.Alive(pids).Any()) break;
            if (!WindowsProcesses.Alive(pids).Any() && sw.Elapsed > TimeSpan.FromSeconds(1)) break;
            await Task.Delay(100);
            CollectReleasedWrappers();
            pids = SnapshotPids();
        }

        report.ExitedEvent = _browserExited.Task.IsCompleted;
        if (killLeftovers)
        {
            foreach (var pid in WindowsProcesses.Alive(pids).ToList())
                if (WindowsProcesses.KillOwn(pid))
                    report.Killed.Add(pid);
        }

        report.StillAlive.AddRange(WindowsProcesses.Alive(pids));
        report.Waited = sw.Elapsed;
        return report;
    }

    public override void WaitForExitNow(TimeSpan timeout, bool killLeftovers)
    {
        var sw = Stopwatch.StartNew();
        CollectReleasedWrappers();
        while (sw.Elapsed < timeout && WindowsProcesses.Alive(SnapshotPids()).Any())
        {
            Thread.Sleep(100);
        }

        if (!killLeftovers) return;
        foreach (var pid in WindowsProcesses.Alive(SnapshotPids()).ToList()) WindowsProcesses.KillOwn(pid);
    }

    /// <summary>Known engine processes of this session plus this app's WebView2 processes not reported yet.</summary>
    private List<int> SnapshotPids()
    {
        lock (_pids)
        {
            foreach (var pid in WindowsProcesses.OwnWebViewProcesses()) _pids.Add(pid);
            return _pids.ToList();
        }
    }

    private static void CollectReleasedWrappers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private sealed class PendingNavigation(Uri url)
    {
        private readonly TaskCompletionSource<NavigationResult> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri Url { get; } = url;
        public ulong? NavigationId { get; set; }
        public int ProxyAuthRetries { get; set; }
        public Task<NavigationResult> Task => _tcs.Task;
        public bool IsCompleted => _tcs.Task.IsCompleted;

        private readonly TaskCompletionSource _documentReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DocumentReady => _documentReady.Task;

        public void SetDocumentReady()
        {
            _documentReady.TrySetResult();
        }

        public void Complete(NavigationResult result)
        {
            _tcs.TrySetResult(result);
        }
    }
}
