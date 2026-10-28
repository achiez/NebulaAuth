using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model;

namespace NebulaAuth.Utility;

public enum ClipboardBitmapStatus
{
    /// <summary>The clipboard has no data.</summary>
    Empty,

    /// <summary>The clipboard has data, but no image (text, other files, ...).</summary>
    NoImage,

    /// <summary>An image was advertised but could not be read or decoded.</summary>
    InvalidImage,
    Image
}

/// <param name="Bitmap">Set when <paramref name="Status" /> is <see cref="ClipboardBitmapStatus.Image" />.</param>
public sealed record ClipboardBitmap(ClipboardBitmapStatus Status, Bitmap? Bitmap = null);

/// <summary>
///     System clipboard access through the main window (Avalonia clipboard is asynchronous).
///     Errors are logged and reported with the common "Error" snackbar.
/// </summary>
public class ClipboardHelper
{
    private const long MAX_IMAGE_FILE_SIZE = 64 * 1024 * 1024;
    private static readonly string[] ImageFileExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

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
    ///     Empties the clipboard. Errors are logged (no snackbar).
    /// </summary>
    public static async Task ClearAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => GetClipboard().ClearAsync());
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex);
        }
    }

    /// <summary>Text currently on the clipboard (null when there is none). Errors are logged (no snackbar).</summary>
    public static async Task<string?> GetTextAsync()
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => GetClipboard().TryGetTextAsync());
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex);
            return null;
        }
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

    /// <summary>
    ///     Reads an image from the clipboard: a bitmap (screenshot tools, "Copy image"), or a single image file copied in
    ///     a file manager. The caller owns and disposes <see cref="ClipboardBitmap.Bitmap" />.
    ///     Errors are logged and reported as <see cref="ClipboardBitmapStatus.InvalidImage" /> (no snackbar).
    /// </summary>
    public static async Task<ClipboardBitmap> GetBitmapAsync()
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                using var data = await GetClipboard().TryGetDataAsync();
                if (data == null || data.Formats.Count == 0) return new ClipboardBitmap(ClipboardBitmapStatus.Empty);

                if (data.Contains(DataFormat.Bitmap))
                {
                    var bitmap = await data.TryGetBitmapAsync();
                    return bitmap != null
                        ? new ClipboardBitmap(ClipboardBitmapStatus.Image, bitmap)
                        : new ClipboardBitmap(ClipboardBitmapStatus.InvalidImage);
                }

                if (data.Contains(DataFormat.File))
                {
                    var files = await data.TryGetFilesAsync();
                    var path = files is {Length: 1} ? files[0].TryGetLocalPath() : null;
                    if (path != null && IsImageFile(path)) return LoadImageFile(path);
                }

                return new ClipboardBitmap(ClipboardBitmapStatus.NoImage);
            });
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Failed to read an image from the clipboard");
            return new ClipboardBitmap(ClipboardBitmapStatus.InvalidImage);
        }
    }

    private static bool IsImageFile(string path)
    {
        return ImageFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static ClipboardBitmap LoadImageFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MAX_IMAGE_FILE_SIZE)
            return new ClipboardBitmap(ClipboardBitmapStatus.InvalidImage);
        return new ClipboardBitmap(ClipboardBitmapStatus.Image, new Bitmap(path));
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
