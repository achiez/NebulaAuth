#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NebulaAuth.Model;
using NebulaAuth.Model.Browser;
using NebulaAuth.ViewModel;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     Debug builds only: NEBULA_BROWSER_TEST=&lt;script file&gt; drives the embedded browser without input and writes
///     NEBULA_BROWSER_TEST_OUT/report.jsonl (+ window-only PNG captures). One command per line:
///     <c>open|openasync &lt;login&gt; [timeoutSec]</c>, <c>reopen &lt;login&gt;</c>, <c>openother &lt;login&gt; confirm|cancel</c>,
///     <c>nav &lt;name&gt; &lt;url&gt;</c>, <c>eval &lt;name&gt; &lt;js expression&gt;</c>, <c>evalasync &lt;name&gt; &lt;async js body&gt;</c>,
///     <c>blank &lt;name&gt; &lt;url&gt;</c> (target=_blank click), <c>winopen &lt;name&gt; &lt;url&gt;</c>, <c>cookies &lt;name&gt;</c>,
///     <c>fwdtest &lt;name&gt;</c>, <c>info &lt;name&gt;</c>, <c>shot &lt;name&gt;</c>, <c>mainshot &lt;name&gt;</c>, <c>wait &lt;ms&gt;</c>,
///     <c>waitclosed [sec]</c>, <c>close</c>, <c>exit</c>. Cookie and token values are never written.
/// </summary>
internal static class BrowserDebugScript
{
    private const string PAGE_INFO =
        "(function(){var id=(typeof g_steamID==='undefined')?null:g_steamID;var acc=(typeof g_AccountID==='undefined')?null:g_AccountID;" +
        "return JSON.stringify({url:location.href,title:document.title,g_steamID:id,g_AccountID:acc," +
        "accountPulldown:!!document.querySelector('#account_pulldown'),loginLink:!!document.querySelector('a[href*=\"/login\"]')," +
        "idInHtml:document.documentElement.innerHTML.indexOf('__EXPECTED__')>=0,text:(document.body?document.body.innerText:'').slice(0,400)});})()";

    private static string _out = ".";
    private static string _expected = "";

    public static void StartIfRequested(MainVM vm)
    {
        var script = Environment.GetEnvironmentVariable("NEBULA_BROWSER_TEST");
        if (string.IsNullOrWhiteSpace(script)) return;
        _out = Environment.GetEnvironmentVariable("NEBULA_BROWSER_TEST_OUT") ?? Path.GetDirectoryName(script)!;
        Directory.CreateDirectory(_out);
        _ = RunAsync(vm, File.ReadAllLines(script));
    }

    private static async Task RunAsync(MainVM vm, string[] lines)
    {
        await Task.Delay(1500);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0];
            var a1 = parts.Length > 1 ? parts[1] : "";
            var a2 = parts.Length > 2 ? parts[2] : "";
            var sw = Stopwatch.StartNew();
            var rec = new Dictionary<string, object?> {["cmd"] = cmd, ["arg"] = a1};
            try
            {
                await ExecuteAsync(vm, cmd, a1, a2, rec);
            }
            catch (Exception ex)
            {
                rec["exception"] = ex.GetType().Name + ": " + ex.Message;
            }

            rec["ms"] = sw.ElapsedMilliseconds;
            Write(rec);
        }
    }

    private static async Task ExecuteAsync(MainVM vm, string cmd, string a1, string a2, Dictionary<string, object?> rec)
    {
        var session = SteamBrowser.Current;
        var engine = session?.Engine;
        switch (cmd)
        {
            case "open":
            case "openasync":
            {
                var maf = vm.MaFiles.First(m => m.AccountName == a1);
                _expected = (maf.SessionData?.SteamId ?? maf.SteamId).Steam64.ToUlong().ToString();
                rec["expectedSteamId"] = _expected;
                rec["proxy"] = (maf.Proxy?.Data ?? MaClient.DefaultProxy) is { } pd
                    ? UpstreamProxy.FromProxyData(pd).ToString()
                    : "none";
                vm.SelectedMafile = maf;
                vm.OpenInBrowserCommand.Execute(maf);
                if (cmd == "openasync") return;
                goto case "waitready";
            }
            case "waitready":
            {
                var maf = vm.MaFiles.First(m => m.AccountName == a1);
                _expected = (maf.SessionData?.SteamId ?? maf.SteamId).Steam64.ToUlong().ToString();
                var timeout = TimeSpan.FromSeconds(a2.Length > 0 ? int.Parse(a2) : 150);
                var s = await WaitForSessionAsync(a1, TimeSpan.FromSeconds(10));
                rec["sessionFound"] = s != null;
                if (s == null) return;
                var sw = Stopwatch.StartNew();
                var dialogs = new List<string>();
                rec["dialogs"] = dialogs;
                while (s.State == BrowserSessionState.Preparing && sw.Elapsed < timeout)
                {
                    // A sign-in dialog (session could not be renewed): captured, then cancelled like a user would
                    if (DialogService.GetSession() is {IsEnded: false, Content: { } content} &&
                        content is not View.Dialogs.WaitLoginDialog)
                    {
                        var type = content.GetType().Name;
                        dialogs.Add(type);
                        await Task.Delay(1200);
                        await CaptureAsync(AppHost.MainWindow, $"{a1}-{type}", rec);
                        await CaptureAsync(s.Window, $"{a1}-{type}-browserwindow", new Dictionary<string, object?>());
                        DialogService.Close(false);
                    }

                    await Task.Delay(200);
                }

                if (s.State != BrowserSessionState.Ready) await Task.WhenAny(s.Closed, Task.Delay(30000));
                rec["state"] = s.State.ToString();
                rec["failure"] = s.Failure?.Error.ToString();
                rec["failureDetail"] = s.Failure?.Message;
                rec["failureUserDetail"] = s.Failure?.UserDetail;
                rec["engine"] = s.Engine?.EngineName;
                rec["timings"] = new Dictionary<string, long>(s.StageTimings);
                if (OperatingSystem.IsWindows()) rec["ua"] = (s.Engine as WebView2Engine)?.UserAgent;
                rec["profile"] = s.ProfilePath ?? s.LastProfilePath;
                rec["forwarderPort"] = s.Forwarder?.Port;
                rec["currentUrl"] = s.Engine?.CurrentUrl;
                if (s.State == BrowserSessionState.Closed) AddCleanup(s, rec);
                break;
            }
            case "reopen":
            {
                var before = SteamBrowser.Current;
                var maf = vm.MaFiles.First(m => m.AccountName == a1);
                vm.OpenInBrowserCommand.Execute(maf);
                await Task.Delay(1500);
                rec["sameSession"] = ReferenceEquals(before, SteamBrowser.Current);
                rec["state"] = SteamBrowser.Current?.State.ToString();
                rec["windowActive"] = SteamBrowser.Current?.Window?.IsActive;
                rec["dialogOpen"] = DialogService.IsDialogOpen;
                break;
            }
            case "openother":
            {
                var before = SteamBrowser.Current;
                var maf = vm.MaFiles.First(m => m.AccountName == a1);
                vm.SelectedMafile = maf;
                vm.OpenInBrowserCommand.Execute(maf);
                var sw = Stopwatch.StartNew();
                while (!DialogService.IsDialogOpen && sw.ElapsedMilliseconds < 5000) await Task.Delay(100);
                rec["confirmShown"] = DialogService.IsDialogOpen;
                await Task.Delay(800);
                await CaptureAsync(AppHost.MainWindow, "switch-confirm", rec);
                DialogService.Close(a2 == "confirm");
                await Task.Delay(500);
                if (a2 == "confirm")
                {
                    if (before != null) await Task.WhenAny(before.Closed, Task.Delay(40000));
                    rec["previousState"] = before?.State.ToString();
                    if (before != null) AddCleanup(before, rec, "previous.");
                }
                else
                {
                    rec["sameSession"] = ReferenceEquals(before, SteamBrowser.Current);
                    rec["state"] = SteamBrowser.Current?.State.ToString();
                }

                break;
            }
            case "nav":
            {
                if (engine == null) throw new InvalidOperationException("no session");
                var r = await engine.NavigateAsync(new Uri(a2), TimeSpan.FromSeconds(60), default);
                rec["ok"] = r.IsSuccess;
                rec["error"] = r.Error;
                rec["page"] = await PageInfoAsync(engine);
                break;
            }
            case "eval":
                if (engine == null) throw new InvalidOperationException("no session");
                rec["result"] = await engine.ExecuteScriptAsync(a2);
                break;
            case "evalasync":
                if (engine == null) throw new InvalidOperationException("no session");
                rec["result"] = await EvalAsync(engine, a2, 30000);
                break;
            case "blank":
            case "winopen":
            {
                if (engine == null) throw new InvalidOperationException("no session");
                var js = cmd == "blank"
                    ? $"(function(){{var a=document.createElement('a');a.href={JsonSerializer.Serialize(a2)};a.target='_blank';a.rel='noopener';document.body.appendChild(a);a.click();return 1;}})()"
                    : $"(function(){{window.open({JsonSerializer.Serialize(a2)},'_blank');return 1;}})()";
                var before = engine.CurrentUrl;
                await engine.ExecuteScriptAsync(js);
                var sw = Stopwatch.StartNew();
                while (engine.CurrentUrl == before && sw.ElapsedMilliseconds < 30000) await Task.Delay(200);
                await Task.Delay(3000);
                rec["before"] = before;
                rec["after"] = engine.CurrentUrl;
                rec["page"] = await PageInfoAsync(engine);
                rec["windowCount"] = Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                    ? d.Windows.Count
                    : -1;
                break;
            }
            case "cookies":
                if (OperatingSystem.IsWindows() && engine is WebView2Engine wv) rec["cookies"] = await wv.DebugDescribeCookiesAsync();
                break;
            case "info":
                rec["profile"] = session?.ProfilePath;
                rec["profileExists"] = session?.ProfilePath is { } p && Directory.Exists(p);
                rec["lockExists"] = session?.ProfilePath is { } p2 && File.Exists(p2 + ".lock");
                rec["forwarderPort"] = session?.Forwarder?.Port;
                if (OperatingSystem.IsWindows()) rec["processes"] = (engine as WebView2Engine)?.DebugProcesses();
                if (OperatingSystem.IsWindows()) rec["ownWebViewProcesses"] = WindowsProcesses.OwnWebViewProcesses();
                rec["profileRootEntries"] = Directory.Exists(BrowserProfileFolder.Root)
                    ? Directory.GetFileSystemEntries(BrowserProfileFolder.Root).Select(Path.GetFileName).ToList()
                    : null;
                break;
            case "fwdtest":
            {
                var port = session?.Forwarder?.Port ?? throw new InvalidOperationException("no forwarder");
                rec["noCredentials"] = await RawConnectAsync(port, null);
                rec["wrongCredentials"] = await RawConnectAsync(port, "Basic " +
                                                                      Convert.ToBase64String(
                                                                          Encoding.ASCII.GetBytes("x:y")));
                break;
            }
            case "ipcheck":
            {
                // Same forwarder path as the browser (with its local credentials) vs. a direct request (reference only)
                var fwd = session?.Forwarder ?? throw new InvalidOperationException("no forwarder");
                using (var h = new System.Net.Http.HttpClientHandler
                       {
                           Proxy = new WebProxy(fwd.ProxyServer)
                               {Credentials = new NetworkCredential(fwd.LocalUser, fwd.LocalPassword)},
                           UseProxy = true
                       })
                using (var hc = new System.Net.Http.HttpClient(h) {Timeout = TimeSpan.FromSeconds(30)})
                {
                    var ips = new List<string>();
                    for (var i = 0; i < 3; i++)
                    {
                        try
                        {
                            ips.Add((await hc.GetStringAsync("https://api.ipify.org")).Trim());
                        }
                        catch (Exception ex)
                        {
                            ips.Add("ERR " + ex.GetBaseException().Message);
                        }
                    }

                    rec["viaForwarder"] = ips;
                }

                using (var direct = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler {UseProxy = false})
                           {Timeout = TimeSpan.FromSeconds(30)})
                {
                    try
                    {
                        rec["direct"] = (await direct.GetStringAsync("https://api.ipify.org")).Trim();
                    }
                    catch (Exception ex)
                    {
                        rec["direct"] = "ERR " + ex.GetBaseException().Message;
                    }
                }

                break;
            }
            case "text":
                if (engine == null) throw new InvalidOperationException("no session");
                rec["text"] = Decode(await engine.ExecuteScriptAsync("document.body?document.body.innerText:null"));
                break;
            case "shot":
                await CaptureAsync(session?.Window is {IsVisible: true} w ? w : AppHost.MainWindow, a1, rec);
                break;
            case "mainshot":
                await CaptureAsync(AppHost.MainWindow, a1, rec);
                break;
            case "wait":
                await Task.Delay(int.Parse(a1));
                break;
            case "waitclosed":
            {
                if (session == null) break;
                await Task.WhenAny(session.Closed, Task.Delay(TimeSpan.FromSeconds(a1.Length > 0 ? int.Parse(a1) : 60)));
                rec["state"] = session.State.ToString();
                rec["failure"] = session.Failure?.Error.ToString();
                AddCleanup(session, rec);
                break;
            }
            case "close":
            {
                if (session == null) throw new InvalidOperationException("no session");
                var sw = Stopwatch.StartNew();
                await session.CloseAsync();
                rec["closeMs"] = sw.ElapsedMilliseconds;
                AddCleanup(session, rec);
                break;
            }
            case "closewin":
            {
                // Like the user closing the window (title bar X), also while it is still loading
                if (session?.Window == null) throw new InvalidOperationException("no window");
                var sw = Stopwatch.StartNew();
                session.Window.Close();
                await Task.WhenAny(session.Closed, Task.Delay(40000));
                rec["closeMs"] = sw.ElapsedMilliseconds;
                rec["state"] = session.State.ToString();
                rec["failure"] = session.Failure?.Error.ToString();
                AddCleanup(session, rec);
                break;
            }
            case "exit":
                AppHost.MainWindow?.Close();
                break;
            default:
                throw new InvalidOperationException("unknown command " + cmd);
        }
    }

    private static void AddCleanup(BrowserSession s, Dictionary<string, object?> rec, string prefix = "")
    {
        rec[prefix + "profileDeleted"] = s.LastProfileDeleted;
        rec[prefix + "profilePath"] = s.LastProfilePath;
        rec[prefix + "profileExistsNow"] = s.LastProfilePath != null && Directory.Exists(s.LastProfilePath);
        rec[prefix + "lockExistsNow"] = s.LastProfilePath != null && File.Exists(s.LastProfilePath + ".lock");
        var r = s.LastExitReport;
        rec[prefix + "exit"] = r == null
            ? null
            : new
            {
                processes = r.Processes, stillAlive = r.StillAlive, killed = r.Killed, exitedEvent = r.ExitedEvent,
                waitedMs = (long) r.Waited.TotalMilliseconds
            };
        if (OperatingSystem.IsWindows()) rec[prefix + "ownWebViewProcessesNow"] = WindowsProcesses.OwnWebViewProcesses();
    }

    private static async Task<BrowserSession?> WaitForSessionAsync(string login, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (SteamBrowser.Current is { } s && s.AccountName == login) return s;
            if (SteamBrowser.Last is { } last && last.AccountName == login) return last;
            await Task.Delay(50);
        }

        return null;
    }

    private static async Task<object?> PageInfoAsync(BrowserEngine engine)
    {
        var raw = await engine.ExecuteScriptAsync(PAGE_INFO.Replace("__EXPECTED__", _expected));
        return Decode(raw);
    }

    private static object? Decode(string? raw)
    {
        if (raw == null) return null;
        try
        {
            var s = raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw) : raw;
            return s == null ? null : JsonSerializer.Deserialize<JsonElement>(s);
        }
        catch
        {
            return raw;
        }
    }

    private static async Task<object?> EvalAsync(BrowserEngine engine, string body, int timeoutMs)
    {
        var id = "__t" + Guid.NewGuid().ToString("N");
        await engine.ExecuteScriptAsync(
            $"window.{id}=undefined;(async()=>{{{body}}})().then(r=>window.{id}=JSON.stringify(r===undefined?null:r)).catch(e=>window.{id}=JSON.stringify({{error:String(e)}}));0");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = await engine.ExecuteScriptAsync($"window.{id}===undefined?null:window.{id}");
            if (r != null && r != "null") return Decode(r);
            await Task.Delay(200);
        }

        return "timeout";
    }

    private static async Task<string> RawConnectAsync(int port, string? auth)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        var req = "CONNECT steamcommunity.com:443 HTTP/1.1\r\nHost: steamcommunity.com:443\r\n" +
                  (auth != null ? "Proxy-Authorization: " + auth + "\r\n" : "") + "\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(req));
        var head = await UpstreamConnector.ReadHeadAsync(s, default);
        return head.Split("\r\n")[0];
    }

    private static void Write(Dictionary<string, object?> rec)
    {
        File.AppendAllText(Path.Combine(_out, "report.jsonl"), JsonSerializer.Serialize(rec) + Environment.NewLine);
    }

    // ===== window-only capture (PrintWindow of the window's own HWND) =====

    private static async Task CaptureAsync(Window? window, string name, Dictionary<string, object?> rec)
    {
        await Task.Delay(300);
        if (window == null || !OperatingSystem.IsWindows())
        {
            rec["shot"] = "no window";
            return;
        }

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var path = Path.Combine(_out, name + ".png");
        rec["shot"] = CaptureHwnd(hwnd, path) ? path : "capture failed";
    }

    private static unsafe bool CaptureHwnd(IntPtr hwnd, string path)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint) Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32
        };
        var dib = CreateDIBSection(mem, ref bi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        try
        {
            // PW_RENDERFULLCONTENT: includes DirectComposition content (the WebView2 surface)
            if (!PrintWindow(hwnd, mem, 2)) return false;
            using var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
            {
                var src = (byte*) bits;
                for (var y = 0; y < h; y++)
                {
                    var dst = (byte*) fb.Address + y * fb.RowBytes;
                    Buffer.MemoryCopy(src + y * w * 4, dst, fb.RowBytes, w * 4);
                    for (var x = 0; x < w; x++) dst[x * 4 + 3] = 255;
                }
            }

            bmp.Save(path, PngBitmapEncoderOptions.Default);
            return true;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
#endif
