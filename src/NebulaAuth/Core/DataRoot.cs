using System;
using System.IO;
using System.Linq;
using NLog;
using Velopack.Locators;

namespace NebulaAuth.Core;

/// <summary>
///     How the data folder was chosen (logged at startup).
/// </summary>
public enum DataRootSource
{
    EnvironmentOverride,

    /// <summary>Windows: the Velopack root (the program folder with NebulaAuth.exe, Update.exe and current\).</summary>
    VelopackRoot,

    /// <summary>Windows: the exe folder of a build that is not Velopack-managed (development, flat folder).</summary>
    ExeFolder,

    /// <summary>macOS / Linux: a "NebulaAuth Data" folder next to the .app / AppImage.</summary>
    PortableMarker,

    /// <summary>macOS / Linux: the default per-user data folder.</summary>
    UserDataFolder,

    /// <summary>Not resolved: macOS runs the app from a translocated (temporary, read-only) copy.</summary>
    None
}

public enum DataRootPlatform
{
    Windows,
    MacOS,
    Linux
}

/// <summary>
///     Everything the data folder resolution reads from the process and the OS, so the rules can be checked for any
///     platform and path layout. File system checks (marker folder, data files) still use the real file system.
/// </summary>
public sealed record DataRootEnvironment
{
    public required DataRootPlatform Platform { get; init; }

    /// <summary><see cref="AppContext.BaseDirectory" />: the exe folder (inside the .app / the mounted AppImage).</summary>
    public required string BaseDirectory { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    /// <summary>
    ///     macOS / Linux: the default per-user data folder (null: <see cref="DataRoot.GetUserDataFolder" /> of
    ///     <see cref="Platform" />). Not used on Windows.
    /// </summary>
    public string? UserDataFolder { get; init; }

    /// <summary>Velopack root (Windows: the folder with Update.exe; macOS: the .app), null when not Velopack-managed.</summary>
    public string? VelopackRootAppDir { get; init; }

    public static DataRootEnvironment FromCurrentProcess()
    {
        var locator = VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null;
        var isManaged = locator?.CurrentlyInstalledVersion != null && !string.IsNullOrEmpty(locator.RootAppDir);
        var platform = OperatingSystem.IsWindows() ? DataRootPlatform.Windows
            : OperatingSystem.IsMacOS() ? DataRootPlatform.MacOS
            : DataRootPlatform.Linux;
        return new DataRootEnvironment
        {
            Platform = platform,
            BaseDirectory = AppContext.BaseDirectory,
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            VelopackRootAppDir = isManaged ? locator!.RootAppDir : null
        };
    }
}

/// <summary>
///     Result of <see cref="DataRoot.Evaluate" />. <see cref="Path" /> is null only when macOS runs a translocated copy
///     (<see cref="TranslocatedApp" />). <see cref="Rule" /> explains the choice for the startup log.
/// </summary>
public sealed record DataRootResolution(string? Path, DataRootSource Source, string Rule, string? TranslocatedApp = null);

/// <summary>
///     The folder with user data (maFiles*, settings, proxies, MAAC, export templates, update settings, logs, custom
///     background). Resolved once at startup before anything touches the disk; every data path is absolute and built from
///     <see cref="Path" />. The process working directory is never used for data: under Velopack it is the replaceable
///     <c>current</c> folder, and a shortcut may start the app from any folder.
///     <list type="number">
///         <item><c>NEBULA_DATA_DIR</c> overrides everything.</item>
///         <item>Windows: always the program folder. Velopack-managed: the Velopack root (the folder with
///             NebulaAuth.exe, Update.exe and <c>current</c>); not Velopack-managed (development build, flat folder): the
///             exe folder.</item>
///         <item>macOS, started from a translocated copy (Gatekeeper App Translocation, the .app was not moved out of
///             the folder it was downloaded / unpacked to): no folder, the user is asked to move the app.</item>
///         <item>macOS / Linux portable copy: a folder named <see cref="PORTABLE_MARKER" /> next to NebulaAuth.app /
///             the AppImage (non-AppImage run: next to the exe) is the data folder itself.</item>
///         <item>macOS: <c>~/Library/Application Support/NebulaAuth</c>; Linux: <c>$XDG_DATA_HOME/NebulaAuth</c> or
///             <c>~/.local/share/NebulaAuth</c> (the .app and the AppImage are replaced on update / read-only).</item>
///     </list>
/// </summary>
public static partial class DataRoot
{
    public const string ENV_OVERRIDE = "NEBULA_DATA_DIR";

    /// <summary>
    ///     Visible folder next to NebulaAuth.app / the AppImage that makes the copy portable (exact name).
    /// </summary>
    public const string PORTABLE_MARKER = "NebulaAuth Data";

    public const string LOG_DIR_GDC_ITEM = "NebulaLogDir";
    private const string TRANSLOCATION_SEGMENT = "AppTranslocation";
    private const string APP_FOLDER_NAME = "NebulaAuth";

    private static string? _path;

    /// <summary>
    ///     Absolute path of the data folder. Throws before the folder is resolved (a data access that early is a bug).
    /// </summary>
    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            // The XAML previewer instantiates views without Program.Main
            if (Avalonia.Controls.Design.IsDesignMode) return AppContext.BaseDirectory;
            throw new InvalidOperationException("The data folder is not resolved yet");
        }
    }

    public static DataRootSource Source { get; private set; } = DataRootSource.None;

    /// <summary>
    ///     Which rule chose the folder (or why there is none), for the startup log.
    /// </summary>
    public static string Rule { get; private set; } = "not resolved";

    /// <summary>
    ///     Not null when macOS runs a translocated copy of the app (path of that .app): the app must not start, the user
    ///     has to move NebulaAuth.app out of the download folder first.
    /// </summary>
    public static string? TranslocatedApp { get; private set; }

    public static string Describe()
    {
        return $"{_path ?? "<unresolved>"} ({Source}: {Rule})";
    }

    /// <summary>
    ///     Must run after <c>VelopackApp.Run()</c> (locator) and before any data access.
    /// </summary>
    public static void Resolve()
    {
        var environment = DataRootEnvironment.FromCurrentProcess();
        ApplyDebugEnvironment(ref environment);
        var result = Evaluate(environment);
        Source = result.Source;
        Rule = result.Rule;
        TranslocatedApp = result.TranslocatedApp;
        if (result.Path == null) return;

        _path = result.Path;
        // NLog.config writes to ${gdc:item=NebulaLogDir}: logs belong to the data folder, not to the program folder
        GlobalDiagnosticsContext.Set(LOG_DIR_GDC_ITEM, System.IO.Path.Combine(_path, "logs"));
    }

    /// <summary>
    ///     Debug builds: replaces parts of the environment to simulate another OS / layout.
    /// </summary>
    static partial void ApplyDebugEnvironment(ref DataRootEnvironment environment);

    /// <summary>
    ///     The resolution rules without side effects (only reads the file system).
    /// </summary>
    public static DataRootResolution Evaluate(DataRootEnvironment env)
    {
        var exeFolder = TrimSeparator(env.BaseDirectory);
        var overrideDir = env.GetEnvironmentVariable(ENV_OVERRIDE);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return new DataRootResolution(TrimSeparator(System.IO.Path.GetFullPath(overrideDir.Trim())),
                DataRootSource.EnvironmentOverride, $"{ENV_OVERRIDE} is set");
        }

        return env.Platform switch
        {
            DataRootPlatform.MacOS => EvaluateMac(env, exeFolder),
            DataRootPlatform.Linux => EvaluateLinux(env, exeFolder),
            _ => EvaluateWindows(env, exeFolder)
        };
    }

    private static DataRootResolution EvaluateWindows(DataRootEnvironment env, string exeFolder)
    {
        // Windows ships only as a portable folder: the data always lives in the program folder.
        // A Velopack Setup install (program in %LOCALAPPDATA%) would need its own rule here.
        if (string.IsNullOrEmpty(env.VelopackRootAppDir))
        {
            return new DataRootResolution(exeFolder, DataRootSource.ExeFolder, "not Velopack-managed: exe folder");
        }

        return new DataRootResolution(TrimSeparator(env.VelopackRootAppDir), DataRootSource.VelopackRoot,
            "Velopack root (program folder)");
    }

    private static DataRootResolution EvaluateMac(DataRootEnvironment env, string exeFolder)
    {
        // The locator knows the .app when Velopack manages the app; otherwise derive it from the exe folder
        // (NebulaAuth.app/Contents/MacOS)
        var bundle = FindAppBundle(env.VelopackRootAppDir) ?? FindAppBundle(exeFolder);
        if (IsTranslocated(bundle ?? exeFolder))
        {
            return new DataRootResolution(null, DataRootSource.None,
                $"macOS App Translocation: the app runs from a temporary read-only copy ({bundle ?? exeFolder})",
                bundle ?? exeFolder);
        }

        var home = bundle != null ? ParentOf(bundle) ?? exeFolder : exeFolder;
        var what = bundle != null ? $"next to {System.IO.Path.GetFileName(bundle)}" : "next to the exe (not in a .app)";
        return EvaluateMarker(env, home, what);
    }

    private static DataRootResolution EvaluateLinux(DataRootEnvironment env, string exeFolder)
    {
        var appImage = env.GetEnvironmentVariable("APPIMAGE");
        var appImageFolder = !string.IsNullOrWhiteSpace(appImage) && System.IO.Path.IsPathRooted(appImage)
            ? ParentOf(appImage)
            : null;
        var home = appImageFolder ?? exeFolder;
        var what = appImageFolder != null ? "next to the AppImage" : "next to the exe (not an AppImage)";
        return EvaluateMarker(env, home, what);
    }

    private static DataRootResolution EvaluateMarker(DataRootEnvironment env, string home, string what)
    {
        var marker = System.IO.Path.Combine(home, PORTABLE_MARKER);
        // Directory.Exists follows symbolic links: a link to a folder works as the marker
        if (Directory.Exists(marker))
        {
            return new DataRootResolution(marker, DataRootSource.PortableMarker,
                $"portable: \"{PORTABLE_MARKER}\" folder found {what} in {home}");
        }

        var userData = env.UserDataFolder ?? GetUserDataFolder(env.Platform);
        return new DataRootResolution(TrimSeparator(userData), DataRootSource.UserDataFolder,
            $"user data folder: no \"{PORTABLE_MARKER}\" folder {what} in {home}");
    }

    /// <summary>
    ///     Path of the first "*.app" folder in <paramref name="path" /> (either separator), or null.
    /// </summary>
    public static string? FindAppBundle(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/' && path[i] != '\\') continue;
            var segment = path.AsSpan(start, i - start);
            if (segment.Length > 4 && segment.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return path[..i];
            }

            start = i + 1;
        }

        return null;
    }

    /// <summary>
    ///     Gatekeeper runs a quarantined app that was not moved by the user from a random read-only mount under
    ///     <c>/private/var/folders/.../AppTranslocation/</c>: nothing next to it is the user's folder.
    /// </summary>
    public static bool IsTranslocated(string path)
    {
        return path.Split('/', '\\').Contains(TRANSLOCATION_SEGMENT, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Absolute path of a file or folder in the data folder. An existing entry that differs only in case is reused
    ///     (Linux file systems are case-sensitive, data copied from Windows may be named "MaFiles" or "Settings.json").
    /// </summary>
    public static string Combine(string name)
    {
        return FindChild(Path, name) ?? System.IO.Path.Combine(Path, name);
    }

    /// <summary>
    ///     Resolves a path entered by the user (export folder): relative paths are relative to the data folder, not to the
    ///     working directory (the replaceable program folder under Velopack).
    /// </summary>
    public static string ResolveUserPath(string path)
    {
        return System.IO.Path.GetFullPath(path, Path);
    }

    /// <summary>
    ///     Creates the folder when needed and checks that files can be created and deleted in it.
    /// </summary>
    public static bool IsWritable(string folder, out Exception? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(folder);
            var probe = System.IO.Path.Combine(folder, $".nebula-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    ///     Default per-user data folder of macOS / Linux (Windows has no such rule: the data is in the program folder).
    /// </summary>
    public static string GetUserDataFolder(DataRootPlatform platform)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (platform == DataRootPlatform.MacOS)
        {
            return System.IO.Path.Combine(home, "Library", "Application Support", APP_FOLDER_NAME);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrWhiteSpace(xdg) && System.IO.Path.IsPathRooted(xdg)
            ? xdg
            : System.IO.Path.Combine(home, ".local", "share");
        return System.IO.Path.Combine(dataHome, APP_FOLDER_NAME);
    }

    /// <summary>
    ///     Case-insensitive lookup of an existing child entry; the exact name wins.
    /// </summary>
    private static string? FindChild(string folder, string name)
    {
        try
        {
            var exact = System.IO.Path.Combine(folder, name);
            if (File.Exists(exact) || Directory.Exists(exact)) return exact;
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFileSystemEntries(folder)
                .FirstOrDefault(e => string.Equals(System.IO.Path.GetFileName(e), name,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ParentOf(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(TrimSeparator(path));
        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    private static string TrimSeparator(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || (root != null && trimmed.Length < root.Length) ? path : trimmed;
    }
}
