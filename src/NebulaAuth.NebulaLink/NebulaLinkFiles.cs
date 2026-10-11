using System;
using System.IO;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     How nebula.json and nebula-history.json are published. The app replaces the default with its verified atomic
///     writer (temporary file, flush, read-back check, rename); the default writes a temporary file and renames it.
/// </summary>
public static class NebulaLinkFiles
{
    /// <summary>Writes the complete content of a file atomically (the target never holds a partial write).</summary>
    public static Action<string, byte[]> Write { get; set; } = DefaultWrite;

    /// <summary>Deletes a file; never throws.</summary>
    public static Action<string> TryDelete { get; set; } = DefaultTryDelete;

    private static void DefaultWrite(string path, byte[] data)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(true);
            }

            File.Move(temp, full, true);
        }
        catch
        {
            DefaultTryDelete(temp);
            throw;
        }
    }

    private static void DefaultTryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
