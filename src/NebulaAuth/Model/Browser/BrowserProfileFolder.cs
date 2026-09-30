using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaAuth.Model.Browser;

/// <summary>
///     Temporary engine profile folder of one embedded browser session: <c>%TEMP%\nbwv\s-&lt;12 hex&gt;</c>, created
///     fresh for every open and deleted after the browser processes exited.
///     <para>
///         Next to the folder, <c>s-&lt;hex&gt;.lock</c> is held open with <see cref="FileShare.None" /> for the whole
///         session (share-mode lock on Windows, <c>flock</c> on Linux / macOS) and contains the owner process id. The
///         startup sweep deletes only folders whose lock can be taken, so a folder of a live NebulaAuth process on
///         another data folder is never touched; the OS releases the lock of a crashed process.
///     </para>
/// </summary>
public sealed class BrowserProfileFolder : IDisposable
{
    public const string PREFIX = "s-";
    private const string LOCK_EXTENSION = ".lock";

    /// <summary>
    ///     WebView2 fails with CO_E_SERVER_EXEC_FAILURE (0x80080005) or hangs when its user data folder path is longer than
    ///     about 229 characters: longer paths are refused before the engine starts.
    /// </summary>
    public const int MAX_PATH_LENGTH = 180;

    private FileStream? _lock;

    private BrowserProfileFolder(string path, FileStream lockStream)
    {
        Path = path;
        _lock = lockStream;
    }

    public static string Root => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nbwv");

    public string Path { get; }

    /// <exception cref="PathTooLongException">The temp folder path is too long for the engine.</exception>
    public static BrowserProfileFolder Create()
    {
        var root = Root;
        var name = PREFIX + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, name));
        if (path.Length > MAX_PATH_LENGTH)
            throw new PathTooLongException(
                $"Browser profile path is too long ({path.Length} > {MAX_PATH_LENGTH} characters)");
        Directory.CreateDirectory(root);
        // The lock exists before the folder: a sweep never sees this folder without its held lock
        var lockStream = new FileStream(path + LOCK_EXTENSION, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.None);
        lockStream.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString()));
        lockStream.Flush();
        Directory.CreateDirectory(path);
        return new BrowserProfileFolder(path, lockStream);
    }

    /// <summary>
    ///     Deletes the folder (retries while engine processes still release files) and verifies it is gone. The lock is
    ///     released in any case; a folder that could not be deleted is removed by the next startup sweep.
    /// </summary>
    public async Task<bool> DeleteAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (Directory.Exists(Path))
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (sw.Elapsed > timeout) break;
                await Task.Delay(200);
            }
        }

        var deleted = !Directory.Exists(Path);
        ReleaseLock(deleteLockFile: deleted);
        if (!deleted)
            Shell.Logger.Warn(last, "Browser profile folder {path} could not be deleted; the next start removes it", Path);
        return deleted;
    }

    /// <summary>Synchronous variant for the app exit (bounded wait).</summary>
    public bool DeleteNow(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (Directory.Exists(Path))
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (sw.Elapsed > timeout) break;
                Thread.Sleep(150);
            }
        }

        var deleted = !Directory.Exists(Path);
        ReleaseLock(deleteLockFile: deleted);
        return deleted;
    }

    private void ReleaseLock(bool deleteLockFile)
    {
        if (_lock == null) return;
        var lockPath = _lock.Name;
        _lock.Dispose();
        _lock = null;
        if (!deleteLockFile) return;
        try
        {
            File.Delete(lockPath);
        }
        catch
        {
            // the sweep removes it later
        }
    }

    public void Dispose()
    {
        ReleaseLock(deleteLockFile: !Directory.Exists(Path));
    }

    /// <summary>
    ///     Removes the profile folders of crashed sessions (their lock is free). Folders of live sessions (lock held by a
    ///     running NebulaAuth process) are skipped. Must complete before a new browser session is created.
    /// </summary>
    public static int SweepLeftovers()
    {
        var root = Root;
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root, PREFIX + "*"))
        {
            var lockPath = dir + LOCK_EXTENSION;
            FileStream? held = null;
            try
            {
                held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.None);
            }
            catch (IOException)
            {
                // Held by a live session (this or another NebulaAuth process)
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            using (held)
            {
                try
                {
                    Directory.Delete(dir, true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Shell.Logger.Warn("Leftover browser profile {dir} can't be deleted yet: {error}", dir, ex.Message);
                    continue;
                }
            }

            TryDelete(lockPath);
        }

        // Lock files without a folder (the folder was deleted, the lock file not)
        foreach (var lockFile in Directory.EnumerateFiles(root, PREFIX + "*" + LOCK_EXTENSION))
        {
            if (Directory.Exists(lockFile[..^LOCK_EXTENSION.Length])) continue;
            try
            {
                using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1,
                           FileOptions.None))
                {
                }

                TryDelete(lockFile);
            }
            catch
            {
                // held by a session that is being created right now
            }
        }

        return removed;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // ignored
        }
    }
}
