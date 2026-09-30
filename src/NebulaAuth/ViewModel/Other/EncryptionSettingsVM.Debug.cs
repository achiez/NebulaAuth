#if DEBUG
using System;
using NebulaAuth.Core;
using NebulaAuth.Model.Mafiles.Protection;

namespace NebulaAuth.ViewModel.Other;

public partial class EncryptionSettingsVM
{
    /// <summary>
    ///     Preview only (SettingsView args <c>enc-as-encrypting</c> / <c>enc-as-decrypting</c>): the tab shows this mode
    ///     instead of the real one (unfinished mode change screenshots without breaking a data folder).
    /// </summary>
    internal StorageMode? DebugForcedMode { get; set; }

    // NEBULA_DEBUG_STORAGE_SLOW=<ms>: slows the operation down for progress screenshots (see SlowStorageProgress)
    partial void DebugWrapProgress(ref IProgress<StorageProgress> progress)
    {
        progress = SlowStorageProgress.Wrap(progress)!;
    }

    partial void DebugOverrideMode(ref StorageMode mode)
    {
        if (DebugForcedMode is { } forced) mode = forced;
    }

    internal void DebugRefresh()
    {
        Refresh();
    }
}
#endif
