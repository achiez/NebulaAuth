using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     macOS: WKWebView with a non-persistent data store (nothing on disk). The proxy is set on that data store with
///     <c>WKWebsiteDataStore.proxyConfigurations</c> (macOS 14+) as an HTTP CONNECT proxy to the local forwarder. WebKit
///     does not answer proxy credential challenges reliably (Apple bug), so on macOS the forwarder listens on loopback
///     without local credentials. Before macOS 14 there is no per-view proxy API: a session that needs a proxy is refused.
///     The UA stays the native Safari/WebKit one (a Chrome UA on WebKit would not match the engine's behaviour).
///     <para>UNVERIFIED on a real Mac: compiled on Windows only.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class WkWebViewEngine : AvaloniaWebViewEngine
{
    private const string OBJC = "/usr/lib/libobjc.A.dylib";
    private const string NETWORK = "/System/Library/Frameworks/Network.framework/Network";

    public override string EngineName => "WKWebView";

    public override void EnsureSupported(bool withProxy)
    {
        if (withProxy && !OperatingSystem.IsMacOSVersionAtLeast(14))
            throw new BrowserOpenException(BrowserError.ProxyNotSupported, "macOS 14+ is required for a proxy");
    }

    public override void ConfigureEnvironment(WebViewEnvironmentRequestedEventArgs args, EngineOptions options)
    {
        if (args is not AppleWKWebViewEnvironmentRequestedEventArgs a)
            throw new BrowserOpenException(BrowserError.EngineUnavailable,
                "Expected WKWebView, got " + args.GetType().Name);
        a.NonPersistentDataStore = true;
        a.EnableDevTools = false;
    }

    protected override Task AttachPlatformAsync(NativeWebView view, IPlatformHandle? handle, EngineOptions options)
    {
        if (options.ProxyServer == null) return Task.CompletedTask;
        if (handle is not IAppleWKWebViewPlatformHandle wk)
            throw new BrowserOpenException(BrowserError.ProxyNotSupported, "No WKWebView handle");
        var webView = wk.GetWKWebViewRetained();
        try
        {
            ApplyProxy(webView, options.ProxyPort);
        }
        finally
        {
            objc_msgSend(webView, sel_registerName("release"));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     <c>webView.configuration.websiteDataStore.proxyConfigurations = [nw_proxy_config_create_http_connect(127.0.0.1:port)]</c>
    ///     and reads it back.
    /// </summary>
    private static void ApplyProxy(IntPtr webView, int port)
    {
        var configuration = objc_msgSend(webView, sel_registerName("configuration"));
        var store = objc_msgSend(configuration, sel_registerName("websiteDataStore"));
        if (store == IntPtr.Zero ||
            objc_msgSend_byte(store, sel_registerName("respondsToSelector:"),
                sel_registerName("setProxyConfigurations:")) == 0)
            throw new BrowserOpenException(BrowserError.ProxyNotSupported, "proxyConfigurations not available");

        var endpoint = nw_endpoint_create_host("127.0.0.1", port.ToString());
        var proxy = nw_proxy_config_create_http_connect(endpoint, IntPtr.Zero);
        try
        {
            var array = objc_msgSend(objc_getClass("NSArray"), sel_registerName("arrayWithObject:"), proxy);
            objc_msgSend_void(store, sel_registerName("setProxyConfigurations:"), array);
            var applied = objc_msgSend(store, sel_registerName("proxyConfigurations"));
            if (applied == IntPtr.Zero || objc_msgSend(applied, sel_registerName("count")) != 1)
                throw new BrowserOpenException(BrowserError.ProxyNotSupported, "proxyConfigurations not applied");
        }
        finally
        {
            nw_release(proxy);
            nw_release(endpoint);
        }
    }

    [DllImport(OBJC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(OBJC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(OBJC)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(OBJC)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(OBJC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(OBJC, EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_byte(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(NETWORK)]
    private static extern IntPtr nw_endpoint_create_host(string hostname, string port);

    [DllImport(NETWORK)]
    private static extern IntPtr nw_proxy_config_create_http_connect(IntPtr endpoint, IntPtr tlsOptions);

    [DllImport(NETWORK)]
    private static extern void nw_release(IntPtr obj);
}
