using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NebulaAuth.Model;

namespace NebulaAuth.Utility;

/// <summary>
///     Files of a drag and drop. Platform data can fail to read (macOS reads the dragging pasteboard through native
///     calls): a failure is logged with the offered formats instead of silently refusing the drop.
/// </summary>
public static class DroppedFiles
{
    /// <summary>
    ///     The drag may carry files. True when the formats can't be read: the drop then decides (and reports).
    /// </summary>
    public static bool MayContainFiles(IDataTransfer data)
    {
        try
        {
            return data.Contains(DataFormat.File) || data.Items.Any(i => i.Contains(DataFormat.File));
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "Drag and drop: the formats can't be read");
            return true;
        }
    }

    /// <summary>Local paths of the dropped files and folders (distinct, in drop order).</summary>
    public static string[] GetLocalPaths(IDataTransfer data)
    {
        var paths = new List<string>();
        try
        {
            foreach (var item in data.Items)
            {
                if (!item.Contains(DataFormat.File)) continue;
                try
                {
                    if (ToLocalPath(item.TryGetFile()) is { } path) paths.Add(path);
                }
                catch (Exception ex)
                {
                    Shell.Logger.Warn(ex, "Drag and drop: a dropped file can't be read");
                }
            }
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "Drag and drop: the dropped items can't be read");
        }

        var result = paths.Distinct(StringComparer.Ordinal).ToArray();
        if (result.Length == 0) Shell.Logger.Warn("Drag and drop: no local file in the drop ({formats})", DescribeFormats(data));
        return result;
    }

    /// <summary>The local path of a storage item: its file system path, else its file:// URI.</summary>
    public static string? ToLocalPath(IStorageItem? item)
    {
        if (item == null) return null;
        var path = item.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) return path;
        return ToLocalPath(item.Path);
    }

    /// <summary>The local path of an absolute file:// URI (percent-decoded); null for anything else.</summary>
    public static string? ToLocalPath(Uri? uri)
    {
        if (uri is not {IsAbsoluteUri: true, IsFile: true}) return null;
        var path = uri.LocalPath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>Format names of a drag for the log (no file names).</summary>
    public static string DescribeFormats(IDataTransfer data)
    {
        try
        {
            var formats = data.Formats.Select(f => f.Identifier)
                .Concat(data.Items.SelectMany(i => i.Formats).Select(f => f.Identifier))
                .Distinct(StringComparer.Ordinal);
            return $"{data.Items.Count} item(s): {string.Join(", ", formats)}";
        }
        catch (Exception ex)
        {
            return "formats unavailable: " + ex.GetType().Name;
        }
    }
}
