using System;
using System.IO;

namespace NebulaAuth.Core;

/// <summary>
///     One NebulaAuth process per data folder: an exclusive lock on <c>.nebula-instance.lock</c> in the data folder is
///     held for the whole process lifetime. A second process on the same folder (two copies of the program, or a start
///     while the first one is still running) must not open the storage: two storage layers on one folder would convert
///     and write the same mafiles concurrently (e.g. one enables encryption while the other still writes plain files).
///     <para>
///         The lock is a <see cref="FileStream" /> opened with <see cref="FileShare.None" />: a share-mode lock on
///         Windows, <c>flock(LOCK_EX)</c> on Linux / macOS (advisory, but every NebulaAuth process takes it). The OS
///         releases it when the process ends, also after a crash; the file itself stays and is reused.
///     </para>
/// </summary>
public sealed class DataRootLock : IDisposable
{
    public const string FILE_NAME = ".nebula-instance.lock";

    private static DataRootLock? _current;

    private readonly FileStream _stream;

    private DataRootLock(FileStream stream)
    {
        _stream = stream;
    }

    public string FilePath => _stream.Name;

    /// <summary>
    ///     Takes the lock of <paramref name="dataRoot" />. Null when another process (or another lock of this process)
    ///     holds it.
    /// </summary>
    /// <exception cref="IOException">The lock file can't be created (not a lock conflict).</exception>
    /// <exception cref="UnauthorizedAccessException">No access to the lock file.</exception>
    public static DataRootLock? TryAcquire(string dataRoot)
    {
        var path = Path.Combine(dataRoot, FILE_NAME);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.None);
            return new DataRootLock(stream);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Sharing violation (Windows) / EWOULDBLOCK of flock (Unix): held by another instance
            return null;
        }
    }

    /// <summary>
    ///     Takes the lock of the data folder for the rest of the process lifetime. False when another NebulaAuth process
    ///     uses the folder.
    /// </summary>
    public static bool AcquireForProcess(string dataRoot)
    {
        if (_current != null) return true;
        _current = TryAcquire(dataRoot);
        return _current != null;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
