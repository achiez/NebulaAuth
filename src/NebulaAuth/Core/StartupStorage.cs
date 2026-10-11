using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.View;

namespace NebulaAuth.Core;

public enum StorageOpenResult
{
    /// <summary>The storage is unlocked (when encrypted) and reconciled: the accounts can be loaded.</summary>
    Opened,

    /// <summary>The user quit at the master password prompt.</summary>
    Cancelled,

    /// <summary>The storage can't be opened (damaged state file, newer format); the user was told why.</summary>
    Failed
}

/// <summary>
///     Startup part of the storage: state, master password, reconciliation. Runs after the first startup phase
///     (<see cref="Shell.InitializeBase" />) and before any mafile is loaded (<see cref="Shell.InitializeAccounts" />):
///     an encrypted storage is unlocked before accounts, MAAC or anything else that uses them start.
/// </summary>
public static partial class StartupStorage
{
    public static async Task<StorageOpenResult> OpenAsync(Window splashScreen)
    {
        var storage = MafileStorageService.Current;
        StorageStartupWindow? window = null;
        try
        {
            try
            {
                storage.LoadState();
            }
            catch (StorageAccessException ex)
            {
                Shell.Logger.Error(ex, "Storage state can't be read ({error})", ex.Error);
                window = ShowWindow(splashScreen);
                return await ShowErrorAsync(window, ex);
            }

            Shell.Logger.Info("Storage mode: {mode}{upgrade}", storage.Mode,
                storage.NeedsUpgrade ? " (first 2.x start: legacy Steam passwords are removed from mafiles)" : string.Empty);
            Task? debugUnlock = null;
            if (storage.RequiresUnlock) DebugUnlock(storage, ref debugUnlock);
            if (debugUnlock != null) await debugUnlock;
            if (storage.RequiresUnlock && debugUnlock == null)
            {
                window = ShowWindow(splashScreen);
                var unlocked = await UnlockAsync(window, storage);
                if (unlocked != StorageOpenResult.Opened) return unlocked;
            }

            IProgress<StorageProgress>? progress = null;
            if (storage.Mode is StorageMode.Encrypting or StorageMode.Decrypting)
            {
                // An interrupted enable / disable is finished before the app opens: show what is going on
                window ??= ShowWindow(splashScreen);
                window.ViewModel.ShowProgress(storage.Mode);
                progress = new Progress<StorageProgress>(window.ViewModel.ReportProgress);
                DebugWrapProgress(ref progress);
            }

            var result = await storage.PrepareAsync(progress);
            if (!result.Completed)
            {
                Shell.Logger.Error("Storage mode change could not be finished ({failed} file(s)); retried on the next start",
                    result.Failed.Count);
                // Told before the accounts open (not only in the skipped-files list)
                window ??= ShowWindow(splashScreen);
                if (!await window.ViewModel.ShowUnfinishedAsync(storage.Mode, result.Failed))
                {
                    Shell.Logger.Info("Unfinished storage mode change: the user quit");
                    return StorageOpenResult.Cancelled;
                }
            }
            Task? debugAction = null;
            DebugStartupAction(storage, ref debugAction);
            if (debugAction != null) await debugAction;
            return StorageOpenResult.Opened;
        }
        finally
        {
            if (window != null)
            {
                window.CloseWindow();
                splashScreen.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }
    }

    /// <summary>
    ///     Tells about storage files that were not loaded or not converted to the current mode with a snackbar
    ///     ("Details" opens the list). The list itself is not opened: it can hold hundreds of files, and the status bar
    ///     badge keeps it reachable. Call after the main window is shown.
    /// </summary>
    public static void NotifySkippedFilesIfAny()
    {
        var count = MafileStorageService.Current.SkippedFiles.Count;
        if (count == 0) return;
        SnackbarController.SendSnackbarWithButton(
            string.Format(LocManager.GetOrDefault("Skipped files: {0}", "SkippedFiles", "Snackbar"), count),
            LocManager.GetOrDefault("Details", "SkippedFiles", "Details"),
            () => _ = DialogsController.ShowSkippedFilesDialog(),
            TimeSpan.FromSeconds(SKIPPED_SNACKBAR_SECONDS));
    }

    private const int SKIPPED_SNACKBAR_SECONDS = 10;

    private static StorageStartupWindow ShowWindow(Window splashScreen)
    {
        // The main window (which applies the theme) does not exist yet
        ThemeManager.ApplyTheme(Settings.Instance.GetTheme());
        splashScreen.Hide();
        var window = new StorageStartupWindow();
        window.Show();
        return window;
    }

    private static async Task<StorageOpenResult> ShowErrorAsync(StorageStartupWindow window, StorageAccessException ex)
    {
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.ViewModel.ShowError(ex);
        await closed.Task;
        return StorageOpenResult.Failed;
    }

    private static async Task<StorageOpenResult> UnlockAsync(StorageStartupWindow window, MafileStorageService storage)
    {
        var vm = window.ViewModel;
        while (true)
        {
            var password = await vm.WaitForPasswordAsync();
            if (password == null) return StorageOpenResult.Cancelled;
            try
            {
                await Task.Run(() => storage.Unlock(password));
                return StorageOpenResult.Opened;
            }
            catch (StorageAccessException ex) when (ex.Error == StorageAccessError.WrongPassword)
            {
                Shell.Logger.Warn("Storage unlock: wrong master password");
                vm.ShowWrongPassword();
            }
            catch (StorageAccessException ex)
            {
                Shell.Logger.Error(ex, "Storage can't be unlocked ({error})", ex.Error);
                return await ShowErrorAsync(window, ex);
            }
        }
    }

    static partial void DebugUnlock(MafileStorageService storage, ref Task? unlock);
    static partial void DebugStartupAction(MafileStorageService storage, ref Task? action);
    static partial void DebugWrapProgress(ref IProgress<StorageProgress>? progress);

    /// <summary>
    ///     Short text of a storage that can't be opened (startup window).
    /// </summary>
    public static string DescribeErrorShort(StorageAccessException ex)
    {
        return ex.Error switch
        {
            StorageAccessError.CorruptedState => LocManager.GetOrDefault(
                "The storage state file (storage-state.json) is damaged. NebulaAuth can't tell whether your accounts are encrypted and will not change any file. Restore the file from a backup.",
                "StorageUnlock", "CorruptedState"),
            StorageAccessError.UnsupportedVersion => LocManager.GetOrDefault(
                "The accounts were encrypted by a newer version of NebulaAuth. Update NebulaAuth to open them.",
                "StorageUnlock", "UnsupportedVersion"),
            StorageAccessError.StateMissing => LocManager.GetOrDefault(
                "storage-state.json is missing, but the data folder contains encrypted mafiles. NebulaAuth can't open them without it and will not change any file. Restore storage-state.json from a backup of the data folder (or rename storage-state.json.prev if it is there). If the encrypted files are not yours, move them out of the maFiles, maFiles_removed and maFiles_backup folders.",
                "StorageUnlock", "StateMissing"),
            _ => LocManager.GetOrDefault("Wrong master password. Try again.", "StorageUnlock", "WrongPassword")
        };
    }

    /// <summary>
    ///     Text of the startup error for a storage that can't be opened (with technical details).
    /// </summary>
    public static string DescribeError(StorageAccessException ex)
    {
        return DescribeErrorShort(ex) + Environment.NewLine + Environment.NewLine + ex.Message;
    }
}
