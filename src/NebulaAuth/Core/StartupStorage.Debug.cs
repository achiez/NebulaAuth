#if DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles.Protection;

namespace NebulaAuth.Core;

public static partial class StartupStorage
{
    /// <summary>
    ///     Debug builds only: <c>NEBULA_DEBUG_STORAGE_ACTION</c> = <c>enable:&lt;password&gt;</c> | <c>disable</c> |
    ///     <c>change:&lt;old&gt;:&lt;new&gt;</c> runs the operation after the startup reconciliation (result in the log).
    /// </summary>
    private static async Task RunDebugActionAsync(MafileStorageService storage)
    {
        var action = Environment.GetEnvironmentVariable("NEBULA_DEBUG_STORAGE_ACTION");
        if (string.IsNullOrWhiteSpace(action)) return;
        var parts = action.Split(':');
        switch (parts[0])
        {
            case "enable" when parts.Length == 2 && storage.Mode == StorageMode.Plain:
                await storage.EnableEncryptionAsync(parts[1], acceptEncryptedFiles: true);
                break;
            case "disable" when storage.Mode == StorageMode.Encrypted:
                await storage.DisableEncryptionAsync();
                break;
            case "change" when parts.Length == 3 && storage.Mode == StorageMode.Encrypted:
                await Task.Run(() => storage.ChangeMasterPasswordAsync(parts[1], parts[2]));
                Shell.Logger.Info("Debug: master password changed");
                break;
            default:
                Shell.Logger.Warn("Debug storage action ignored in mode {mode}", storage.Mode);
                break;
        }
    }

    static partial void DebugWrapProgress(ref IProgress<StorageProgress>? progress)
    {
        progress = SlowStorageProgress.Wrap(progress);
    }
}

/// <summary>
///     Debug builds only: <c>NEBULA_DEBUG_STORAGE_SLOW=&lt;ms&gt;</c> sleeps that long after every file of a bulk storage
///     operation (progress screenshots of the startup window / Encryption settings tab). The storage reports progress
///     synchronously from its worker thread, so the sleep slows the operation itself.
/// </summary>
internal sealed class SlowStorageProgress(IProgress<StorageProgress> inner, int delayMs) : IProgress<StorageProgress>
{
    public void Report(StorageProgress value)
    {
        inner.Report(value);
        if (value.CurrentFile != null) Thread.Sleep(delayMs);
    }

    public static IProgress<StorageProgress>? Wrap(IProgress<StorageProgress>? progress)
    {
        if (progress == null ||
            !int.TryParse(Environment.GetEnvironmentVariable("NEBULA_DEBUG_STORAGE_SLOW"), out var ms) || ms <= 0)
            return progress;
        return new SlowStorageProgress(progress, ms);
    }
}
#endif
