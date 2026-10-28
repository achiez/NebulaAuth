using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model;

namespace NebulaAuth.Utility;

/// <summary>
///     "Copy mafile" of an encrypted storage: the file clipboard needs a real file, so a readable copy (plain JSON, no
///     Steam password) is written to a private folder under the system temp folder and put on the clipboard. It exists
///     until it is pasted: it is deleted when the clipboard no longer holds it, after <see cref="LIFETIME_MINUTES" />
///     minutes, when another mafile is copied, when the app exits (the clipboard is emptied if it still holds the copy)
///     and copies left by a previous run (crash) are deleted on the next start.
///     <para>
///         Copies are kept per data folder (<c>NebulaAuth-clipboard/&lt;hash of the data folder&gt;</c>): the start-up
///         sweep only deletes copies of this data folder, never those of another NebulaAuth instance that runs on another
///         data folder (one instance per data folder: <see cref="DataRootLock" />).
///     </para>
/// </summary>
/// <remarks>
///     Windows: the temp folder is per user. macOS: <c>$TMPDIR</c> is per user. Linux: <c>/tmp</c> is shared, so the
///     folder name contains the user name and the folder / files are created with 0700 / 0600 permissions; a folder that
///     is a link or can't be restricted is not used.
/// </remarks>
public static partial class DecryptedMafileClipboard
{
    public const int LIFETIME_MINUTES = 10;
    private const string FOLDER_NAME = "NebulaAuth-clipboard";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(LIFETIME_MINUTES);

    private static string? _currentDir;
    private static string? _currentFile;
    private static DateTime _expiresUtc;
    private static DispatcherTimer? _timer;
    private static bool _checking;

    /// <summary>
    ///     Private folder of all NebulaAuth instances of this user.
    /// </summary>
    public static string RootFolder => Path.Combine(Path.GetTempPath(),
        OperatingSystem.IsWindows() ? FOLDER_NAME : $"{FOLDER_NAME}-{Environment.UserName}");

    /// <summary>
    ///     Private folder of the readable copies of this data folder.
    /// </summary>
    public static string Folder => Path.Combine(RootFolder, DataRootTag(DataRoot.Path));

    /// <summary>
    ///     Short stable name of a data folder (first 16 hex digits of SHA-256 of its full path; case-insensitive on
    ///     Windows / macOS, where the file system is).
    /// </summary>
    public static string DataRootTag(string dataRoot)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (!OperatingSystem.IsLinux()) path = path.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16].ToLowerInvariant();
    }

    /// <summary>
    ///     Path of the copy that is on the clipboard now (null when there is none).
    /// </summary>
    public static string? CurrentFile => _currentFile;

    /// <summary>
    ///     Deletes readable copies of this data folder left by a previous run (the app crashed before they expired).
    ///     Call only while holding the data folder lock (<see cref="DataRootLock" />).
    /// </summary>
    public static void SweepLeftovers()
    {
        var folder = Folder;
        try
        {
            if (!Directory.Exists(folder)) return;
            if (IsLink(RootFolder) || IsLink(folder))
            {
                Shell.Logger.Warn("Clipboard copies: {folder} is a link, not touched", folder);
                return;
            }

            Directory.Delete(folder, true);
            Shell.Logger.Info("Clipboard copies of a previous run deleted");
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "Clipboard copies of a previous run can't be deleted");
        }
    }

    /// <summary>
    ///     Writes <paramref name="plainMafile" /> as <paramref name="fileName" /> to a new private folder and puts the file
    ///     on the clipboard. The previous copy is deleted. The buffer is zeroed after writing.
    /// </summary>
    public static async Task<bool> CopyAsync(string fileName, byte[] plainMafile)
    {
        DeleteCurrent();
        string path;
        try
        {
            var dir = Path.Combine(EnsureFolder(), Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
            CreatePrivateDirectory(dir);
            path = Path.Combine(dir, Path.GetFileName(fileName));
            WritePrivateFile(path, plainMafile);
            _currentDir = dir;
            _currentFile = path;
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Can't write the readable copy of a mafile for the clipboard");
            DeleteCurrent();
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainMafile);
        }

        if (!await ClipboardHelper.SetFilesAsync([path]))
        {
            DeleteCurrent();
            return false;
        }

        var lifetime = Lifetime;
        DebugLifetime(ref lifetime);
        _expiresUtc = DateTime.UtcNow + lifetime;
        StartTimer();
        return true;
    }

    /// <summary>
    ///     Deletes the current copy (if any) now.
    /// </summary>
    public static void DeleteCurrent()
    {
        var dir = _currentDir;
        _currentDir = null;
        _currentFile = null;
        _timer?.Stop();
        if (dir == null) return;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            // Retried by the sweep on the next start
            Shell.Logger.Warn(ex, "Can't delete the readable clipboard copy of a mafile");
        }
    }

    /// <summary>
    ///     The app exits: the clipboard is emptied if it still holds the readable copy (a paste after the exit would
    ///     refer to a deleted file) and the copy is deleted. Synchronous (called from the exit of the application
    ///     lifetime): a clipboard that can't answer at once (X11 reads are asynchronous; the clipboard content of an
    ///     exiting X11 app is gone anyway) is left alone.
    /// </summary>
    public static void OnAppExit()
    {
        var file = _currentFile;
        if (file == null) return;
        try
        {
            if (AppHost.TopLevel?.Clipboard is { } clipboard && HoldsFileNow(clipboard, file) == true)
            {
                var clear = clipboard.ClearAsync();
                Shell.Logger.Debug("App exit: clipboard with the readable mafile copy {state}",
                    clear.IsCompletedSuccessfully ? "emptied" : "is being emptied");
            }
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "App exit: the clipboard can't be checked");
        }

        DeleteCurrent();
    }

    // null: the clipboard did not answer synchronously
    private static bool? HoldsFileNow(IClipboard clipboard, string file)
    {
        var task = clipboard.TryGetFilesAsync();
        if (!task.IsCompletedSuccessfully) return null;
        return task.Result?.Any(i => i.TryGetLocalPath() is { } p &&
                                     string.Equals(Path.GetFullPath(p), file, StringComparison.OrdinalIgnoreCase)) ==
               true;
    }

    private static void StartTimer()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer {Interval = CheckInterval};
            _timer.Tick += async (_, _) => await CheckAsync();
        }

        _timer.Start();
    }

    private static async Task CheckAsync()
    {
        var file = _currentFile;
        if (file == null || _checking)
        {
            if (file == null) _timer?.Stop();
            return;
        }

        _checking = true;
        try
        {
            var files = await ClipboardHelper.GetFilesAsync();
            var onClipboard = files.Any(f => string.Equals(Path.GetFullPath(f), file, StringComparison.OrdinalIgnoreCase));
            if (file != _currentFile) return; // replaced meanwhile
            if (!onClipboard)
            {
                Shell.Logger.Debug("Clipboard changed: readable mafile copy deleted");
                DeleteCurrent();
                return;
            }

            if (DateTime.UtcNow < _expiresUtc) return;
            // Expired while still on the clipboard: a later paste would refer to a missing file
            await ClipboardHelper.ClearAsync();
            Shell.Logger.Debug("Readable mafile copy expired and deleted");
            DeleteCurrent();
        }
        finally
        {
            _checking = false;
        }
    }

    private static string EnsureFolder()
    {
        EnsurePrivateFolder(RootFolder);
        return EnsurePrivateFolder(Folder);
    }

    private static string EnsurePrivateFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            if (IsLink(folder)) throw new IOException($"{folder} is a link");
            // Also fails when the folder belongs to another user
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return folder;
        }

        CreatePrivateDirectory(folder);
        return folder;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivateFile(string path, byte[] content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(content);
    }

    static partial void DebugLifetime(ref TimeSpan lifetime);

    private static bool IsLink(string path)
    {
        return new DirectoryInfo(path).LinkTarget != null;
    }
}
