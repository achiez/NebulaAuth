using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using NebulaAuth.Model.Browser;

namespace NebulaAuth.Core.Browser;

/// <summary>Cookie put into the browser before the first navigation (only the selected account's Steam cookies).</summary>
public sealed record BrowserCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure,
    bool HttpOnly,
    DateTimeOffset? Expires);

/// <param name="DocumentReady">
///     The result of <see cref="BrowserEngine.NavigateToDocumentAsync" /> when the HTML was parsed before the page finished
///     loading (the load goes on; <see cref="BrowserEngine.WaitForLoadAsync" /> gives its final result).
/// </param>
public sealed record NavigationResult(bool IsSuccess, string? Error, bool DocumentReady = false);

/// <summary>Settings of one engine start, fixed when the session opens.</summary>
internal sealed class EngineOptions
{
    /// <summary><c>http://127.0.0.1:port</c> of the session's <see cref="LocalForwarder" />; null = no proxy at all.</summary>
    public string? ProxyServer { get; init; }

    public string? ProxyUser { get; init; }
    public string? ProxyPassword { get; init; }
    public int ProxyPort { get; init; }

    /// <summary>BCP 47 language of the app (Accept-Language of the engine), e.g. "ru-RU".</summary>
    public required string Language { get; init; }

    public BrowserProfileFolder? Profile { get; init; }

    /// <summary>1 = first start; 2 = retry after an engine start failure (fresh profile, no GPU).</summary>
    public int Attempt { get; init; } = 1;
}

internal sealed class EngineExitReport
{
    public List<int> Processes { get; } = [];
    public List<int> StillAlive { get; } = [];
    public List<int> Killed { get; } = [];
    public bool ExitedEvent { get; set; }
    public TimeSpan Waited { get; set; }
}

/// <summary>
///     Platform part of the embedded browser: environment isolation, proxy routing, cookies, navigation and the shutdown
///     of the engine processes. <see cref="BrowserSession" /> drives it; all members are called on the UI thread.
/// </summary>
internal abstract class BrowserEngine
{
    /// <summary>Navigation of the page was blocked by the policy (key of the notice text).</summary>
    public event Action<string>? Blocked;

    /// <summary>Address or history changed.</summary>
    public event Action? NavigationStateChanged;

    /// <summary>The engine needs an on-disk profile folder (Windows WebView2).</summary>
    public virtual bool NeedsProfileFolder => false;

    /// <summary>The engine can answer the forwarder's credential challenge (else the forwarder runs without them).</summary>
    public virtual bool ForwarderNeedsCredentials => false;

    public abstract string EngineName { get; }
    public abstract string? CurrentUrl { get; }
    public abstract bool CanGoBack { get; }
    public abstract bool CanGoForward { get; }

    public static BrowserEngine Create()
    {
        if (OperatingSystem.IsWindows()) return new WebView2Engine();
        if (OperatingSystem.IsMacOS()) return new WkWebViewEngine();
        if (OperatingSystem.IsLinux()) return new WebKitGtkEngine();
        throw new BrowserOpenException(BrowserError.EngineUnavailable, "Unsupported platform");
    }

    /// <summary>
    ///     Refuses the start when this platform can't guarantee the routing through the proxy (never opens without it).
    /// </summary>
    public virtual void EnsureSupported(bool withProxy)
    {
    }

    /// <summary>Fills the engine environment (isolated, non-persistent profile, proxy arguments).</summary>
    public abstract void ConfigureEnvironment(WebViewEnvironmentRequestedEventArgs args, EngineOptions options);

    /// <summary>Takes the native engine after the adapter was created: hardening, proxy, event handlers.</summary>
    public abstract Task AttachAsync(NativeWebView view, IPlatformHandle? handle, EngineOptions options);

    /// <summary>Removes all cookies of the (fresh) environment and adds <paramref name="cookies" />.</summary>
    public abstract Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies);

    public abstract Task<NavigationResult> NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    ///     Navigates and returns as soon as the HTML of that navigation is parsed (DOMContentLoaded), or when it completed /
    ///     failed earlier. A failed navigation returns its failure; images and scripts keep loading after a
    ///     <see cref="NavigationResult.DocumentReady" /> result.
    /// </summary>
    public abstract Task<NavigationResult> NavigateToDocumentAsync(Uri url, TimeSpan timeout, CancellationToken ct);

    /// <summary>Completes with the final result of the last <see cref="NavigateToDocumentAsync" /> (no timeout).</summary>
    public abstract Task<NavigationResult>? DocumentLoad { get; }

    /// <summary>Final result of the last <see cref="NavigateToDocumentAsync" /> (completed load or failure).</summary>
    public abstract Task<NavigationResult> WaitForLoadAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Runs a script in the page; returns its result as JSON (null when it can't run).</summary>
    public abstract Task<string?> ExecuteScriptAsync(string script);

    public abstract void GoBack();
    public abstract void GoForward();
    public abstract void Reload();

    /// <summary>Drops the references to native objects (before the view is removed from the window).</summary>
    public abstract void Release();

    /// <summary>
    ///     Waits until the engine processes of this session exited; with <paramref name="killLeftovers" /> the ones still
    ///     alive after the timeout are terminated (only processes started for this session).
    /// </summary>
    public virtual Task<EngineExitReport> WaitForExitAsync(TimeSpan timeout, bool killLeftovers)
    {
        return Task.FromResult(new EngineExitReport {ExitedEvent = true});
    }

    /// <summary>
    ///     Bounded synchronous variant for the app exit: the dispatcher does not run any more, so the engine may not close
    ///     by itself in time; with <paramref name="killLeftovers" /> this session's engine processes are then terminated.
    /// </summary>
    public virtual void WaitForExitNow(TimeSpan timeout, bool killLeftovers)
    {
    }

    protected void RaiseBlocked(string noticeKey)
    {
        Blocked?.Invoke(noticeKey);
    }

    protected void RaiseNavigationStateChanged()
    {
        NavigationStateChanged?.Invoke();
    }
}
