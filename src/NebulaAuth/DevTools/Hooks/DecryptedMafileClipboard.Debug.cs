using System;

namespace NebulaAuth.Utility;

public static partial class DecryptedMafileClipboard
{
    // NEBULA_DEBUG_CLIPBOARD_LIFETIME=<seconds> shortens the lifetime of the readable copy (tests)
    static partial void DebugLifetime(ref TimeSpan lifetime)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("NEBULA_DEBUG_CLIPBOARD_LIFETIME"), out var seconds) &&
            seconds > 0)
            lifetime = TimeSpan.FromSeconds(seconds);
    }
}
