using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NebulaAuth.Core.Browser;

internal sealed partial class WebView2Engine
{
    /// <summary>Debug harness: cookie names, domains and attributes of the store (never the values).</summary>
    internal async Task<List<string>> DebugDescribeCookiesAsync()
    {
        if (_core == null) return [];
        var cookies = await _core.CookieManager.GetCookiesAsync(null);
        return cookies.Select(c =>
                $"{c.Name} @{c.Domain}{c.Path} secure={c.IsSecure} httpOnly={c.IsHttpOnly} sameSite={c.SameSite} session={c.IsSession}")
            .ToList();
    }

    private static int _debugBadEngineStarts;

    /// <summary>
    ///     NEBULA_DEBUG_BROWSER_BAD_ENGINE=1: the first engine start gets a user data folder path longer than WebView2
    ///     accepts (a real engine start failure: CO_E_SERVER_EXEC_FAILURE or no answer); =2: every start.
    /// </summary>
    partial void DebugConfigureEnvironment(Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs args)
    {
        if (!int.TryParse(System.Environment.GetEnvironmentVariable("NEBULA_DEBUG_BROWSER_BAD_ENGINE"), out var count) ||
            _debugBadEngineStarts++ >= count) return;
        args.UserDataFolder = System.IO.Path.Combine(args.UserDataFolder!, new string('x', 120), new string('y', 120));
    }

    internal List<int> DebugProcesses()
    {
        lock (_pids) return _pids.ToList();
    }
}
