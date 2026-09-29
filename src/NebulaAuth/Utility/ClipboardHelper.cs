using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model;

namespace NebulaAuth.Utility;

/// <summary>
///     System clipboard access through the main window (Avalonia clipboard is asynchronous).
///     Errors are logged and reported with the common "Error" snackbar.
/// </summary>
public class ClipboardHelper
{
    /// <summary>
    ///     Puts <paramref name="text" /> on the clipboard. Returns false if it failed.
    ///     The platform operation is asynchronous: on Windows it normally completes synchronously so the result is exact;
    ///     if it is still pending, true is returned and a later failure is logged and reported with a snackbar.
    /// </summary>
    public static bool Set(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.Invoke(() => Set(text));
        }

        try
        {
            var task = GetClipboard().SetTextAsync(text);
            return Observe(task);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        return false;
    }

    /// <summary>
    ///     Puts files (local paths) on the clipboard, like Explorer "Copy", and waits for the result.
    /// </summary>
    public static async Task<bool> SetFilesAsync(IEnumerable<string> files)
    {
        try
        {
            var paths = files.ToArray();
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var items = await FileDialogs.GetStorageItemsAsync(paths);
                if (items.Length == 0)
                    throw new InvalidOperationException("None of the files exist: " + string.Join(", ", paths));
                await GetClipboard().SetFilesAsync(items);
            });
            return true;
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        return false;
    }

    /// <summary>
    ///     Returns local paths of the files currently on the clipboard (empty when there are none).
    ///     Errors are logged (no snackbar).
    /// </summary>
    public static async Task<string[]> GetFilesAsync()
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var items = await GetClipboard().TryGetFilesAsync();
                if (items == null) return [];
                return items
                    .Select(i => i.TryGetLocalPath())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => p!)
                    .ToArray();
            });
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex);
            return [];
        }
    }

    private static IClipboard GetClipboard()
    {
        return AppHost.TopLevel?.Clipboard ?? throw new InvalidOperationException("Clipboard is not available");
    }

    private static bool Observe(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsCompletedSuccessfully) return true;
            ReportError(task.Exception?.GetBaseException() ?? new OperationCanceledException());
            return false;
        }

        task.ContinueWith(t => ReportError(t.Exception?.GetBaseException() ?? new OperationCanceledException()),
            TaskContinuationOptions.NotOnRanToCompletion);
        return true;
    }

    private static void ReportError(Exception ex)
    {
        Shell.Logger.Error(ex);
        SnackbarController.SendSnackbar(LocManager.GetCommonOrDefault("Error", "Error"));
    }
}
