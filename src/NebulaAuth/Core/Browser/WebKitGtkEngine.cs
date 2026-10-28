using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     Linux: WebKitGTK with an ephemeral website data manager (nothing on disk). The proxy is set on that data manager
///     (<c>webkit_website_data_manager_set_network_proxy_settings</c>, custom mode, <c>http://127.0.0.1:port</c>) on the
///     GLib thread before the first load. Avalonia does not expose WebKit's proxy authentication signal, so the forwarder
///     listens on loopback without local credentials. The WPE backend (no proxy API wired here) is refused when a proxy is
///     needed; the GTK backend is requested instead. The UA stays the native WebKitGTK one.
///     <para>UNVERIFIED on Linux: compiled on Windows only.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class WebKitGtkEngine : AvaloniaWebViewEngine
{
    /// <summary>WEBKIT_NETWORK_PROXY_MODE_CUSTOM</summary>
    private const int PROXY_MODE_CUSTOM = 2;

    private static readonly string[] WebKitLibraries = ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37"];

    public override string EngineName => "WebKitGTK";

    public override void ConfigureEnvironment(WebViewEnvironmentRequestedEventArgs args, EngineOptions options)
    {
        switch (args)
        {
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = true;
                gtk.EnableDevTools = false;
                break;
            case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                wpe.PreferWebKitGtkInstead = true;
                wpe.EnableDevTools = false;
                break;
            default:
                throw new BrowserOpenException(BrowserError.EngineUnavailable,
                    "Expected WebKitGTK, got " + args.GetType().Name);
        }
    }

    protected override async Task AttachPlatformAsync(NativeWebView view, IPlatformHandle? handle,
        EngineOptions options)
    {
        if (handle is not IGtkWebViewPlatformHandle gtk)
        {
            // WPE (or unknown): its data directories are persistent and the proxy is not wired
            throw new BrowserOpenException(BrowserError.ProxyNotSupported,
                "Only the WebKitGTK backend is supported, got " + handle?.HandleDescriptor);
        }

        if (options.ProxyServer == null) return;
        var webView = gtk.WebKitWebView;
        var proxyUri = options.ProxyServer;
        var applied = await RunOnGlibThreadAsync(() => ApplyProxy(webView, proxyUri));
        if (!applied) throw new BrowserOpenException(BrowserError.ProxyNotSupported, "WebKitGTK proxy not applied");
    }

    private static unsafe bool ApplyProxy(IntPtr webView, string proxyUri)
    {
        IntPtr lib = IntPtr.Zero;
        foreach (var name in WebKitLibraries)
            if (NativeLibrary.TryLoad(name, out lib))
                break;
        if (lib == IntPtr.Zero) return false;
        if (!NativeLibrary.TryGetExport(lib, "webkit_web_view_get_website_data_manager", out var getManager) ||
            !NativeLibrary.TryGetExport(lib, "webkit_network_proxy_settings_new", out var settingsNew) ||
            !NativeLibrary.TryGetExport(lib, "webkit_website_data_manager_set_network_proxy_settings",
                out var setProxy) ||
            !NativeLibrary.TryGetExport(lib, "webkit_network_proxy_settings_free", out var settingsFree))
            return false;

        var manager = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>) getManager)(webView);
        if (manager == IntPtr.Zero) return false;
        var uri = Encoding.UTF8.GetBytes(proxyUri + "\0");
        IntPtr settings;
        fixed (byte* p = uri)
        {
            // No ignore list: loopback goes through the forwarder too
            settings = ((delegate* unmanaged[Cdecl]<byte*, byte**, IntPtr>) settingsNew)(p, null);
        }

        if (settings == IntPtr.Zero) return false;
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>) setProxy)(manager, PROXY_MODE_CUSTOM, settings);
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, void>) settingsFree)(settings);
        }

        return true;
    }

    /// <summary>
    ///     WebKitGTK must be called on Avalonia's GLib thread (<c>Avalonia.X11.Interop.GtkInteropHelper.RunOnGlibThread</c>,
    ///     public but reached by reflection: Avalonia.X11 is not referenced at compile time).
    /// </summary>
    private static Task<bool> RunOnGlibThreadAsync(Func<bool> action)
    {
        var helper = Type.GetType("Avalonia.X11.Interop.GtkInteropHelper, Avalonia.X11");
        var method = helper?.GetMethod("RunOnGlibThread", BindingFlags.Public | BindingFlags.Static);
        if (method is not {IsGenericMethodDefinition: true})
            throw new BrowserOpenException(BrowserError.ProxyNotSupported, "GLib thread helper not found");
        return (Task<bool>) method.MakeGenericMethod(typeof(bool)).Invoke(null, [action])!;
    }
}
