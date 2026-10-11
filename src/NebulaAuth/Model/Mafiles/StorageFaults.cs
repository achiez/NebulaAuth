using System;
using System.Diagnostics;

namespace NebulaAuth.Model.Mafiles;

/// <summary>
///     Named points of the storage layer where a Debug build can inject a failure to check that the files stay
///     consistent. The calls are removed from Release builds.
/// </summary>
public static partial class StorageFaults
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

    [Conditional("DEBUG")]
    public static void Point(string point, string path)
    {
        OnPoint(point, path);
    }

    /// <summary>Cleanup after a failure is skipped for a simulated crash.</summary>
    public static bool IsSimulatedCrash(Exception ex)
    {
        var simulated = false;
        CheckSimulatedCrash(ex, ref simulated);
        return simulated;
    }

    static partial void OnPoint(string point, string path);
    static partial void CheckSimulatedCrash(Exception ex, ref bool simulated);
}
