using System;

namespace NebulaAuth.Model.Mafiles;

public static partial class StorageFaults
{
    /// <summary>
    ///     Called at every point with (point, path). Throwing <see cref="SimulatedCrashException" /> simulates a process
    ///     crash: temporary files are left behind as a real crash would leave them.
    /// </summary>
    public static Action<string, string>? Hook { get; set; }

    static partial void OnPoint(string point, string path)
    {
        Hook?.Invoke(point, path);
    }

    static partial void CheckSimulatedCrash(Exception ex, ref bool simulated)
    {
        simulated = ex is SimulatedCrashException;
    }
}

public sealed class SimulatedCrashException(string point, string path)
    : Exception($"Simulated crash at {point}: {path}")
{
    public string PointName { get; } = point;
    public string FilePath { get; } = path;
}
