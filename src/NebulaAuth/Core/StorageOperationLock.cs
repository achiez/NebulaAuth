using System.Threading;

namespace NebulaAuth.Core;

/// <summary>
///     Set while a bulk storage operation runs (enabling / disabling encryption, changing the master password). The
///     settings dialog stays open and can't be closed meanwhile; the main window and the tray "Exit" refuse to close the
///     app, so no parallel action starts and the operation is not cut off (it would only be resumed on the next start).
/// </summary>
public static class StorageOperationLock
{
    private static int _count;

    public static bool IsBusy => Volatile.Read(ref _count) > 0;

    public static void Enter()
    {
        Interlocked.Increment(ref _count);
    }

    public static void Exit()
    {
        Interlocked.Decrement(ref _count);
    }

    public static string BusyMessage => LocManager.GetOrDefault(
        "Wait until the encryption operation is finished.", "SettingsDialog", "Encryption", "BusyCantClose");
}
