using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace NebulaAuth.Core;

/// <summary>
///     Native open-file / pick-folder dialogs via the main window <see cref="IStorageProvider" />.
///     Results are local file system paths. Must be called on the UI thread.
/// </summary>
public static class FileDialogs
{
    /// <summary>
    ///     Shows an open-file dialog.
    /// </summary>
    /// <param name="filterName">Display name of the file type filter, e.g. "Mafile".</param>
    /// <param name="patterns">Glob patterns of the filter, e.g. <c>["*.mafile", "*.maFile"]</c>. Empty = all files.</param>
    /// <param name="allowMultiple">Allow selecting several files.</param>
    /// <param name="title">Dialog title (optional).</param>
    /// <returns>Selected local paths; empty array when the user cancelled or no picker is available.</returns>
    public static async Task<string[]> OpenFilesAsync(string filterName, IReadOnlyList<string> patterns,
        bool allowMultiple = false, string? title = null)
    {
        var storage = AppHost.TopLevel?.StorageProvider;
        if (storage is not {CanOpen: true}) return [];

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple
        };
        if (patterns.Count > 0)
        {
            options.FileTypeFilter = [new FilePickerFileType(filterName) {Patterns = patterns}];
        }

        var files = await storage.OpenFilePickerAsync(options);
        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToArray();
    }

    /// <summary>
    ///     Shows a pick-folder dialog.
    /// </summary>
    /// <returns>Selected local folder path, or null when the user cancelled or no picker is available.</returns>
    public static async Task<string?> PickFolderAsync(string? title = null)
    {
        var storage = AppHost.TopLevel?.StorageProvider;
        if (storage is not {CanPickFolder: true}) return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <summary>
    ///     Resolves local paths to storage items (for clipboard / drag-and-drop APIs). Missing files are skipped.
    /// </summary>
    public static async Task<IStorageItem[]> GetStorageItemsAsync(IEnumerable<string> paths)
    {
        var storage = AppHost.TopLevel?.StorageProvider ??
                      throw new InvalidOperationException("Storage provider is not available");
        var result = new List<IStorageItem>();
        foreach (var path in paths)
        {
            var file = await storage.TryGetFileFromPathAsync(path);
            if (file != null) result.Add(file);
        }

        return result.ToArray();
    }
}
