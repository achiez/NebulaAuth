using System;
using System.Diagnostics;
using System.IO;
using NebulaAuth.Core;

namespace NebulaAuth.Theme;

public static partial class Motion
{
    private static readonly Stopwatch TraceClock = Stopwatch.StartNew();

    // NEBULA_MOTION_SLOW=<factor> slows every effect down (frame-by-frame checks)
    static partial void DebugSlowFactor(ref double factor)
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("NEBULA_MOTION_SLOW"), out var slow) && slow > 0)
            factor = slow;
    }

    // NEBULA_MOTION_TRACE=1: frame log of the popup animations in motion-trace.log of the data folder
    static partial void Trace(string text)
    {
        if (Environment.GetEnvironmentVariable("NEBULA_MOTION_TRACE") != "1") return;
        File.AppendAllText(DataRoot.Combine("motion-trace.log"),
            $"{TraceClock.ElapsedMilliseconds,6} {text}{Environment.NewLine}");
    }
}
