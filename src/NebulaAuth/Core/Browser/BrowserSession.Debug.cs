#if DEBUG
using System;
using System.Runtime.InteropServices;

namespace NebulaAuth.Core.Browser;

internal sealed partial class BrowserSession
{
    /// <summary>
    ///     NEBULA_DEBUG_BROWSER_FAIL_START=1: the first engine start fails after the engine was created (like
    ///     CO_E_SERVER_EXEC_FAILURE); =2: both attempts fail.
    /// </summary>
    partial void DebugSimulateStartFailure(int attempt)
    {
        var failures = Environment.GetEnvironmentVariable("NEBULA_DEBUG_BROWSER_FAIL_START");
        if (int.TryParse(failures, out var count) && attempt <= count)
            throw new COMException("Simulated engine start failure", unchecked((int) 0x80080005));
    }

    /// <summary>
    ///     NEBULA_DEBUG_BROWSER_START_TIMEOUT_MS=&lt;ms&gt;: the first engine start is abandoned after that time (the engine
    ///     finishes starting after its view was removed), the retry uses the normal timeout.
    /// </summary>
    static partial void DebugAdjustStartTimeout(ref TimeSpan timeout)
    {
        if (_debugTimeoutUsed || !int.TryParse(Environment.GetEnvironmentVariable("NEBULA_DEBUG_BROWSER_START_TIMEOUT_MS"),
                out var ms)) return;
        _debugTimeoutUsed = true;
        timeout = TimeSpan.FromMilliseconds(ms);
    }

    private static bool _debugTimeoutUsed;

    /// <summary>NEBULA_DEBUG_BROWSER_EXPECT=&lt;steamid64&gt;: the verification expects that account instead.</summary>
    static partial void DebugAdjustExpectedSteamId(ref ulong steamId)
    {
        if (ulong.TryParse(Environment.GetEnvironmentVariable("NEBULA_DEBUG_BROWSER_EXPECT"), out var id)) steamId = id;
    }
}
#endif
