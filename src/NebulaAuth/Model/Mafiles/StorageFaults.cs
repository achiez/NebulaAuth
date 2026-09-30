using System;
using System.Diagnostics;

namespace NebulaAuth.Model.Mafiles;

/// <summary>
///     Debug builds only: named points of the storage layer where a test can inject a failure ("crash") to check that
///     the files stay consistent and that the next start recovers. Calls are removed from Release builds.
/// </summary>
public static class StorageFaults
{
    public const string ATOMIC_WRITTEN = "atomic.written";
    public const string ATOMIC_VERIFIED = "atomic.verified";
    public const string ATOMIC_PUBLISHED = "atomic.published";
    public const string CONVERT_PUBLISHED = "convert.published";
    public const string CONVERT_SOURCE_DELETED = "convert.source-deleted";
    public const string TRANSITION_STATE_SAVED = "transition.state-saved";
    public const string TRANSITION_BEFORE_FINAL_STATE = "transition.before-final-state";
    public const string TRANSITION_BEFORE_PASSWORDS_DELETE = "transition.before-passwords-delete";
    public const string UPGRADE_FILE_DONE = "upgrade.file-done";

#if DEBUG
    /// <summary>
    ///     Called at every point with (point, path). Throwing <see cref="SimulatedCrashException" /> simulates a process
    ///     crash: temporary files are left behind as a real crash would leave them.
    /// </summary>
    public static Action<string, string>? Hook { get; set; }
#endif

    [Conditional("DEBUG")]
    public static void Point(string point, string path)
    {
#if DEBUG
        Hook?.Invoke(point, path);
#endif
    }

    /// <summary>
    ///     Cleanup after a failure is skipped for a simulated crash (Debug only).
    /// </summary>
    public static bool IsSimulatedCrash(Exception ex)
    {
#if DEBUG
        return ex is SimulatedCrashException;
#else
        return false;
#endif
    }
}

#if DEBUG
public sealed class SimulatedCrashException(string point, string path)
    : Exception($"Simulated crash at {point}: {path}")
{
    public string PointName { get; } = point;
    public string FilePath { get; } = path;
}
#endif
