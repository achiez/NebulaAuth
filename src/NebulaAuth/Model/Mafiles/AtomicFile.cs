using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaAuth.Model.Mafiles;

/// <summary>
///     Checks the bytes read back from the temporary file before they are published. Returns an error text, or null
///     when the content is valid.
/// </summary>
public delegate string? AtomicWriteValidator(byte[] writtenBytes);

/// <summary>
///     The one mechanism that publishes files of the storage layer (mafiles, backups, backup ZIPs, storage state, Steam
///     password store): the full content is written to a temporary file in the same folder, flushed to disk, read back
///     and verified, and only then renamed over the target (or published as a new file). A failure before the rename
///     leaves the previous file untouched; the target never contains a partial write.
///     <para>
///         Temporary files are named <c>.&lt;target name&gt;.&lt;random&gt;.nebula-tmp</c>: they never match an
///         account / backup pattern, so they are never loaded as accounts. After a crash they are removed by the
///         storage layer only after the target (or its other-format counterpart) is verified to be intact.
///     </para>
///     Publishing uses <see cref="File.Move(string, string, bool)" />: <c>rename(2)</c> on Linux / macOS and
///     <c>MoveFileEx</c> on Windows, both atomic within one folder (same volume). On Linux / macOS the folder is
///     synced (<c>fsync</c>) after the rename, so the new name itself survives a power loss (Windows NTFS journals the
///     rename metadata; there is no folder handle to sync).
/// </summary>
public static partial class AtomicFile
{
    public const string TEMP_SUFFIX = ".nebula-tmp";
    private const int RANDOM_PART_LENGTH = 8;

    public static bool IsTempFile(string fileName)
    {
        return fileName.EndsWith(TEMP_SUFFIX, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Name of the file a temporary file was created for (in the same folder), or null when the name is not a
    ///     temporary file name of this class.
    /// </summary>
    public static string? GetTempTarget(string tempFileName)
    {
        if (!IsTempFile(tempFileName) || !tempFileName.StartsWith('.')) return null;
        var withoutSuffix = tempFileName[1..^TEMP_SUFFIX.Length];
        var dot = withoutSuffix.LastIndexOf('.');
        if (dot <= 0 || withoutSuffix.Length - dot - 1 != RANDOM_PART_LENGTH) return null;
        return withoutSuffix[..dot];
    }

    /// <summary>
    ///     Writes <paramref name="data" /> to <paramref name="path" /> atomically.
    /// </summary>
    /// <param name="path">Target file.</param>
    /// <param name="data">Complete new content.</param>
    /// <param name="overwrite">
    ///     false: publish only as a new file (an existing target is kept and <see cref="IOException" /> is thrown).
    /// </param>
    /// <param name="validator">Additional check of the bytes read back (e.g. decrypt / parse).</param>
    public static void Write(string path, ReadOnlySpan<byte> data, bool overwrite = true,
        AtomicWriteValidator? validator = null)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = CreateTempPath(fullPath);
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                fs.Write(data);
                // FlushFileBuffers (Windows) / fsync (Unix)
                fs.Flush(true);
            }

            StorageFaults.Point(StorageFaults.ATOMIC_WRITTEN, fullPath);
            var readBack = File.ReadAllBytes(temp);
            Verify(readBack, data, validator, fullPath);
            StorageFaults.Point(StorageFaults.ATOMIC_VERIFIED, fullPath);
            File.Move(temp, fullPath, overwrite);
            SyncDirectory(fullPath);
            StorageFaults.Point(StorageFaults.ATOMIC_PUBLISHED, fullPath);
        }
        catch (Exception ex)
        {
            if (!StorageFaults.IsSimulatedCrash(ex)) TryDelete(temp);
            throw;
        }
    }

    /// <inheritdoc cref="Write" />
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> data, bool overwrite = true,
        AtomicWriteValidator? validator = null, CancellationToken token = default)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = CreateTempPath(fullPath);
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await fs.WriteAsync(data, token).ConfigureAwait(false);
                await fs.FlushAsync(token).ConfigureAwait(false);
                fs.Flush(true);
            }

            StorageFaults.Point(StorageFaults.ATOMIC_WRITTEN, fullPath);
            var readBack = await File.ReadAllBytesAsync(temp, token).ConfigureAwait(false);
            Verify(readBack, data.Span, validator, fullPath);
            StorageFaults.Point(StorageFaults.ATOMIC_VERIFIED, fullPath);
            token.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite);
            SyncDirectory(fullPath);
            StorageFaults.Point(StorageFaults.ATOMIC_PUBLISHED, fullPath);
        }
        catch (Exception ex)
        {
            if (!StorageFaults.IsSimulatedCrash(ex)) TryDelete(temp);
            throw;
        }
    }

    private static void Verify(byte[] readBack, ReadOnlySpan<byte> expected, AtomicWriteValidator? validator,
        string target)
    {
        if (!readBack.AsSpan().SequenceEqual(expected))
        {
            throw new IOException($"Verification of the written file failed (content differs): {target}");
        }

        var error = validator?.Invoke(readBack);
        if (error != null)
        {
            throw new IOException($"Verification of the written file failed ({error}): {target}");
        }
    }

    private static string CreateTempPath(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath) ??
                  throw new ArgumentException("The path has no folder", nameof(fullPath));
        Directory.CreateDirectory(dir);
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(RANDOM_PART_LENGTH / 2)).ToLowerInvariant();
        return Path.Combine(dir, "." + Path.GetFileName(fullPath) + "." + random + TEMP_SUFFIX);
    }

    /// <summary>
    ///     Linux / macOS: <c>fsync</c> of the folder of <paramref name="filePath" />, so a rename / delete in it is on
    ///     disk. Best effort: a failure is logged (the file content itself was already flushed).
    /// </summary>
    public static void SyncDirectory(string filePath)
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var fd = Unix.Open(dir, Unix.O_RDONLY);
            if (fd < 0)
            {
                LogSyncFailure(dir, Marshal.GetLastPInvokeError());
                return;
            }

            try
            {
                if (Unix.Fsync(fd) != 0) LogSyncFailure(dir, Marshal.GetLastPInvokeError());
            }
            finally
            {
                Unix.Close(fd);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Shell.Logger.Warn(ex, "Folder sync is not available");
        }
    }

    private static void LogSyncFailure(string dir, int errno)
    {
        Shell.Logger.Warn("Folder sync failed for {dir}: errno {errno}", dir, errno);
    }

    private static partial class Unix
    {
        // O_RDONLY is 0 on Linux and macOS; a folder can be opened read-only for fsync on both
        public const int O_RDONLY = 0;

        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int Open(string path, int flags);

        [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static partial int Fsync(int fd);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        public static partial int Close(int fd);
    }

    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
