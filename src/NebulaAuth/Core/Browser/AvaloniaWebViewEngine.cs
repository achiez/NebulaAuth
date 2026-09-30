using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     Engine parts that go through the cross-platform <see cref="NativeWebView" /> API (macOS WKWebView, Linux
///     WebKitGTK): navigation, new windows, the navigation policy, cookies and scripts. The platform subclasses isolate
///     the data store and route the engine through the local forwarder.
///     <para>
///         UNVERIFIED: written against the Avalonia.Controls.WebView 12.1 API and the WebKit documentation, compiled on
///         Windows, not run on macOS / Linux yet.
///     </para>
/// </summary>
internal abstract class AvaloniaWebViewEngine : BrowserEngine
{
    private static readonly TimeSpan DocumentPollInterval = TimeSpan.FromMilliseconds(100);
    private PendingNavigation? _documentNavigation;
    private PendingNavigation? _navigation;
    protected NativeWebView? View { get; private set; }

    public override string? CurrentUrl => View?.Source?.AbsoluteUri;
    public override bool CanGoBack => View?.CanGoBack == true;
    public override bool CanGoForward => View?.CanGoForward == true;

    /// <summary>Called before any navigation; must throw when the proxy can't be applied.</summary>
    protected abstract Task AttachPlatformAsync(NativeWebView view, Avalonia.Platform.IPlatformHandle? handle,
        EngineOptions options);

    public override async Task AttachAsync(NativeWebView view, Avalonia.Platform.IPlatformHandle? handle,
        EngineOptions options)
    {
        View = view;
        await AttachPlatformAsync(view, handle, options);
        view.NavigationStarted += OnNavigationStarted;
        view.NavigationCompleted += OnNavigationCompleted;
        view.NewWindowRequested += OnNewWindowRequested;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        switch (NavigationPolicy.Check(e.Request))
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

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        RaiseNavigationStateChanged();
        _navigation?.TrySetResult(new NavigationResult(e.IsSuccess, e.IsSuccess ? null : "NavigationFailed"));
        _documentNavigation?.TrySetResult(new NavigationResult(e.IsSuccess, e.IsSuccess ? null : "NavigationFailed"));
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        // Links opening a new window stay in this view (same environment and cookies)
        e.Handled = true;
        if (e.Request != null && NavigationPolicy.Check(e.Request) == NavigationDecision.Allow)
            View?.Navigate(e.Request);
    }

    public override Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies)
    {
        var manager = View?.TryGetCookieManager() ??
                      throw new BrowserOpenException(BrowserError.EngineUnavailable, "No cookie manager");
        // The data store is non-persistent and new: it starts empty
        foreach (var c in cookies)
        {
            var cookie = new Cookie(c.Name, c.Value, c.Path, c.Domain)
            {
                Secure = c.Secure,
                HttpOnly = c.HttpOnly
            };
            if (c.Expires is { } expires) cookie.Expires = expires.UtcDateTime;
            manager.AddOrUpdateCookie(cookie);
        }

        return Task.CompletedTask;
    }

    public override async Task<NavigationResult> NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        var view = View ?? throw new InvalidOperationException("Engine not attached");
        var pending = new PendingNavigation();
        _navigation = pending;
        try
        {
            view.Navigate(url);
            return await pending.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            View?.Stop();
            return new NavigationResult(false, "Timeout");
        }
        finally
        {
            if (_navigation == pending) _navigation = null;
        }
    }

    /// <summary>
    ///     No DOMContentLoaded event in the cross-platform API: the page is polled until the document of the target origin
    ///     is no longer "loading" (UNVERIFIED on macOS / Linux).
    /// </summary>
    public override async Task<NavigationResult> NavigateToDocumentAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        var view = View ?? throw new InvalidOperationException("Engine not attached");
        var pending = new PendingNavigation();
        _documentNavigation = pending;
        var origin = url.GetLeftPart(UriPartial.Authority);
        var script = "JSON.stringify({href:location.href,state:document.readyState})";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        view.Navigate(url);
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (pending.Task.IsCompleted) return pending.Task.Result;
            try
            {
                var raw = await view.InvokeScript(script);
                if (raw != null && raw.StartsWith('"')) raw = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (raw != null)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    var href = doc.RootElement.GetProperty("href").GetString() ?? "";
                    var state = doc.RootElement.GetProperty("state").GetString();
                    // The previous document (about:blank) is "complete" too: only the target origin counts
                    if (href.StartsWith(origin, StringComparison.OrdinalIgnoreCase) && state != "loading")
                        return new NavigationResult(true, null, DocumentReady: true);
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                                           or KeyNotFoundException)
            {
                // document between loads: next poll
            }

            await Task.Delay(DocumentPollInterval, ct);
        }

        View?.Stop();
        return new NavigationResult(false, "Timeout");
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
            View?.Stop();
            return new NavigationResult(false, "Timeout");
        }
    }

    public override async Task<string?> ExecuteScriptAsync(string script)
    {
        return View == null ? null : await View.InvokeScript(script);
    }

    public override void GoBack()
    {
        View?.GoBack();
    }

    public override void GoForward()
    {
        View?.GoForward();
    }

    public override void Reload()
    {
        View?.Refresh();
    }

    public override void Release()
    {
        _navigation?.TrySetResult(new NavigationResult(false, "Closed"));
        _documentNavigation?.TrySetResult(new NavigationResult(false, "Closed"));
        if (View != null)
        {
            View.NavigationStarted -= OnNavigationStarted;
            View.NavigationCompleted -= OnNavigationCompleted;
            View.NewWindowRequested -= OnNewWindowRequested;
        }

        View = null;
    }

    private sealed class PendingNavigation : TaskCompletionSource<NavigationResult>
    {
        public PendingNavigation() : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
        }
    }
}
