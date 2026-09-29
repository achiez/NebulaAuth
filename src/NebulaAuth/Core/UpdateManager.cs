using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using NebulaAuth.Core.Updates;
using NebulaAuth.Model;
using NebulaAuth.Model.Update;
using Newtonsoft.Json;
using NLog;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Logging;
using VelopackUpdateInfo = Velopack.UpdateInfo;
using VelopackUpdateManager = Velopack.UpdateManager;

namespace NebulaAuth.Core;

/// <summary>
///     Checks the signed feed (<see cref="SignedFeedSource" />), downloads and installs updates with Velopack.
///     <list type="bullet">
///         <item>States: <see cref="UpdateStatus" /> (Idle, Checking, UpToDate, Available, Downloading, ReadyToRestart,
///             Failed with a <see cref="UpdateFailureKind" /> and a link to the manual package).</item>
///         <item>Nothing is installed without an explicit user action (<see cref="StartDownload" />,
///             <see cref="ApplyAndRestart" />); Velopack's auto-apply on startup is off (Program.Main).</item>
///         <item>Before downloading and installing: write access to the program folder (no elevation prompt is ever
///             triggered), free space, no other NebulaAuth process in the folder.</item>
///         <item>An <see cref="ApplyAttempt" /> file lets the next start detect an installation that did not happen.</item>
///         <item>Builds that are not Velopack-managed (development, flat folders) only check and offer the download page.</item>
///     </list>
///     All public members are called on the UI thread; <see cref="Status" /> changes on the UI thread.
/// </summary>
public static partial class UpdateManager
{
    // Download: full package (+ .partial copy while downloading, deltas); install: the extracted package next to the old
    // "current" folder. The extracted package is about 2.5 times the size of the compressed one.
    private const double REQUIRED_SPACE_FACTOR = 3.0;
    private const long REQUIRED_SPACE_MARGIN = 100L * 1024 * 1024;
    private const int SHUTDOWN_FALLBACK_MS = 15000;

    private static readonly HttpClient ChangelogHttp = new() {Timeout = TimeSpan.FromSeconds(15)};

    private static VelopackUpdateManager? _velopack;
    private static VelopackUpdateInfo? _pendingUpdate;
    private static CancellationTokenSource? _downloadCts;
    private static ApplyAttempt? _failedAttempt;
    private static string? _updatedToVersion;
    private static VelopackAsset? _applyOnExit;
    private static int _applyStarted;

    public static UpdateStatus Status { get; } = new();

    public static bool HasPendingUpdate { get; private set; }

    /// <summary>
    ///     Raised on the UI thread when an automatic check found a version the user asked to be reminded about later.
    /// </summary>
    public static event Action? PendingUpdateDetected;

    private static IVelopackLocator? Locator => VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null;

    /// <summary>
    ///     True when this copy was installed from a release package and can update itself.
    /// </summary>
    public static bool IsInstalled => Locator?.CurrentlyInstalledVersion != null;

    public static string InstalledVersionText => InstalledVersion.ToString();

    private static SemanticVersion InstalledVersion
    {
        get
        {
            if (Locator?.CurrentlyInstalledVersion is { } velopackVersion) return velopackVersion;
            var assemblyVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new SemanticVersion(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(assemblyVersion.Build, 0));
        }
    }

    /// <summary>
    ///     Update channel of this OS / architecture: win-x64, osx-x64, osx-arm64, linux-x64 (release packages are built
    ///     with these channel names). "-beta" is appended for the beta feed.
    /// </summary>
    public static string PlatformChannel
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64"
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>
    ///     Channel to check: the one of the installed package unless update.json selects "stable" / "beta".
    /// </summary>
    private static string Channel
    {
        get
        {
            var selected = UpdateSettings.Load().Channel;
            if (string.Equals(selected, "beta", StringComparison.OrdinalIgnoreCase)) return PlatformChannel + "-beta";
            if (string.Equals(selected, "stable", StringComparison.OrdinalIgnoreCase)) return PlatformChannel;
            return Locator?.Channel is {Length: > 0} packed ? packed : PlatformChannel;
        }
    }

    /// <summary>
    ///     Called once by <see cref="Shell.Initialize" /> (logger and data folder are ready): reports the result of an
    ///     installation started by the previous run.
    /// </summary>
    public static void Initialize()
    {
        StartDebugHooks();
        Status.InstalledVersion = InstalledVersionText;
        Status.CanInstall = IsInstalled;
        Status.ReleasePageUrl = UpdateTrust.RELEASES_PAGE_URL;
        Status.ManualUrl = UpdateTrust.RELEASES_PAGE_URL;
        if (Locator is { } locator)
        {
            locator.AddLogger(new NLogVelopackLogger());
            Shell.Logger.Info("Velopack: installed {version}, channel {channel}, root {root}, portable {portable}",
                locator.CurrentlyInstalledVersion, locator.Channel, locator.RootAppDir, locator.IsPortable);
        }

        var attempt = ApplyAttempt.Take();
        if (attempt == null) return;
        if (SemanticVersion.TryParse(attempt.ToVersion, out var target) && InstalledVersion >= target)
        {
            Shell.Logger.Info("Update {from} -> {to} installed", attempt.FromVersion, attempt.ToVersion);
            _updatedToVersion = attempt.ToVersion;
            return;
        }

        Shell.Logger.Error("Update {from} -> {to} (started {time:O}) was not installed: still running {installed}",
            attempt.FromVersion, attempt.ToVersion, attempt.StartedAt, InstalledVersionText);
        _failedAttempt = attempt;
    }

    /// <summary>
    ///     Test hooks of debug builds (UpdateManager.Debug.cs); removed from release builds.
    /// </summary>
    static partial void StartDebugHooks();

    static partial void AdjustDebugCheck(ref bool manual);

    public static void CheckForUpdates(bool manual = false)
    {
        AdjustDebugCheck(ref manual);
        _ = CheckForUpdatesAsync(manual);
    }

    public static void SkipVersion(string version)
    {
        var settings = UpdateSettings.Load();
        settings.SkipVersion(version);
    }

    public static void SetRemindAfter(TimeSpan delay)
    {
        var settings = UpdateSettings.Load();
        settings.SetRemindAfter(DateTime.Now + delay);
    }

    /// <summary>
    ///     Opens the update dialog with the current state (download progress, ready to restart, failure).
    /// </summary>
    public static Task ShowDialog()
    {
        return DialogsController.ShowUpdateDialog(Status);
    }

    private static async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            // The startup check is started by the MainVM constructor, before the window (and its dialog host) exists
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            if (_failedAttempt is { } failed)
            {
                _failedAttempt = null;
                Status.AvailableVersion = failed.ToVersion;
                SetFailed(UpdateFailureKind.ApplyFailed,
                    $"{failed.FromVersion} -> {failed.ToVersion}, started {failed.StartedAt.LocalDateTime:g}",
                    failed.ManualUrl, failed.ReleasePageUrl);
                await ShowDialog();
                return;
            }

            if (_updatedToVersion is { } updated)
            {
                _updatedToVersion = null;
                SnackbarController.SendSnackbar(string.Format(
                    LocManager.GetCodeBehindOrDefault("NebulaAuth was updated to version {0}", "UpdateService", "Updated"),
                    updated));
            }

            switch (Status.State)
            {
                case UpdateState.Checking:
                    return;
                case UpdateState.Downloading or UpdateState.ReadyToRestart:
                    if (manual) await ShowDialog();
                    return;
            }

            Status.State = UpdateState.Checking;
            CheckResult result;
            try
            {
                result = await Task.Run(CheckCoreAsync);
            }
            catch (Exception ex)
            {
                var kind = Classify(ex);
                Shell.Logger.Error(ex, "Update check failed ({kind})", kind);
                if (!manual)
                {
                    // Automatic check: no dialog for a network problem at every start; the log keeps the reason
                    Status.State = UpdateState.Idle;
                    return;
                }

                SetFailed(kind, ex.Message, null, null);
                await ShowDialog();
                return;
            }

            ApplyCheckResult(result);
            if (result.Version == null)
            {
                Status.State = UpdateState.UpToDate;
                if (manual)
                {
                    SnackbarController.SendSnackbar(LocManager.GetCodeBehindOrDefault(
                        "You are using the latest version", "UpdateService", "LatestVersion"));
                }

                return;
            }

            await LoadChangelogAsync(result.Version);
            if (!manual && !UpdateSettings.Load().ShouldShow(result.Version))
            {
                HasPendingUpdate = true;
                PendingUpdateDetected?.Invoke();
                return;
            }

            await ShowDialog();
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Error while checking updates");
        }
    }

    private sealed record CheckResult(
        string? Version,
        long Size,
        FeedMetadata? Metadata,
        VelopackUpdateManager? Velopack,
        VelopackUpdateInfo? Update,
        bool AlreadyDownloaded);

    private static async Task<CheckResult> CheckCoreAsync()
    {
        var channel = Channel;
        var source = CreateSource();
        Shell.Logger.Info("Checking for updates: installed {version}, channel {channel}", InstalledVersionText, channel);

        if (!IsInstalled)
        {
            // Not Velopack-managed: only tell the user about a newer version (manual download)
            var feed = await source.GetReleaseFeed(new NLogVelopackLogger(), SignedFeedSource.PACKAGE_ID, channel);
            var latest = feed.Assets.Where(a => a.Type == VelopackAssetType.Full).MaxBy(a => a.Version);
            return latest != null && latest.Version > InstalledVersion
                ? new CheckResult(latest.Version.ToString(), latest.Size, source.LastMetadata, null, null, false)
                : new CheckResult(null, 0, source.LastMetadata, null, null, false);
        }

        var packedChannel = Locator?.Channel;
        var switchesChannel = !string.Equals(packedChannel, channel, StringComparison.Ordinal);
        var options = new UpdateOptions
        {
            ExplicitChannel = switchesChannel ? channel : null,
            // Leaving the beta channel may go back to an older stable version
            AllowVersionDowngrade = switchesChannel
        };
        var velopack = new VelopackUpdateManager(source, options);
        var update = await velopack.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update == null)
        {
            return new CheckResult(null, 0, source.LastMetadata, velopack, null, false);
        }

        var target = update.TargetFullRelease;
        var downloaded = velopack.UpdatePendingRestart is { } pending && pending.Version == target.Version;
        Shell.Logger.Info("Update available: {version} ({size} bytes, {deltas} delta(s)), downloaded: {downloaded}",
            target.Version, target.Size, update.DeltasToTarget.Length, downloaded);
        return new CheckResult(target.Version.ToString(), target.Size, source.LastMetadata, velopack, update,
            downloaded);
    }

    private static void ApplyCheckResult(CheckResult result)
    {
        _velopack = result.Velopack;
        _pendingUpdate = result.Update;
        Status.CanInstall = result.Velopack != null;
        Status.FailureKind = UpdateFailureKind.None;
        Status.FailureDetail = null;
        Status.Progress = 0;
        Status.AvailableVersion = result.Version;
        Status.DownloadSize = result.Size;
        Status.ReleasePageUrl = result.Metadata?.ReleaseUrl ?? UpdateTrust.RELEASES_PAGE_URL;
        Status.ManualUrl = result.Metadata?.ManualUrl ?? Status.ReleasePageUrl;
        if (result.Version != null)
        {
            Status.State = result.AlreadyDownloaded ? UpdateState.ReadyToRestart : UpdateState.Available;
        }
    }

    /// <summary>
    ///     "Download and install": downloads the update in the background; the dialog shows the progress.
    /// </summary>
    public static void StartDownload()
    {
        _ = DownloadAsync();
    }

    private static async Task DownloadAsync()
    {
        if (Status.State != UpdateState.Available || _velopack == null || _pendingUpdate == null) return;
        var velopack = _velopack;
        var update = _pendingUpdate;
        try
        {
            CheckPreconditions(update.TargetFullRelease.Size, false);
        }
        catch (UpdateException ex)
        {
            Shell.Logger.Error(ex, "Update download refused ({kind})", ex.Kind);
            SetFailed(ex.Kind, ex.Message, null, null);
            return;
        }

        Status.Progress = 0;
        Status.State = UpdateState.Downloading;
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        try
        {
            Shell.Logger.Info("Downloading update {version}", update.TargetFullRelease.Version);
            await Task.Run(() => velopack.DownloadUpdatesAsync(update,
                p => Dispatcher.UIThread.Post(() => Status.Progress = p), cts.Token));
            Shell.Logger.Info("Update {version} downloaded", update.TargetFullRelease.Version);
            Status.Progress = 100;
            Status.State = UpdateState.ReadyToRestart;
            NotifyIfDialogClosed(string.Format(LocManager.GetCodeBehindOrDefault(
                "Update {0} is downloaded and ready to install", "UpdateService", "Downloaded"), Status.AvailableVersion));
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            Shell.Logger.Info(ex, "Update download cancelled");
            Status.Progress = 0;
            Status.State = UpdateState.Available;
        }
        catch (Exception ex)
        {
            var kind = Classify(ex);
            Shell.Logger.Error(ex, "Update download failed ({kind})", kind);
            SetFailed(kind, ex.Message, null, null);
            NotifyIfDialogClosed(LocManager.GetCodeBehindOrDefault("The update could not be downloaded",
                "UpdateService", "DownloadFailed"));
        }
        finally
        {
            _downloadCts = null;
        }
    }

    /// <summary>
    ///     The download runs on after the dialog was closed: tell the user when it ends, with a button that reopens it.
    /// </summary>
    private static void NotifyIfDialogClosed(string text)
    {
        if (DialogService.GetSession()?.Content is View.Dialogs.UpdateDialog) return;
        SnackbarController.SendSnackbarWithButton(text,
            LocManager.GetCodeBehindOrDefault("Details", "UpdateService", "OpenDialog"),
            () => _ = ShowDialog(), TimeSpan.FromSeconds(15));
    }

    public static void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    /// <summary>
    ///     "Restart and update": closes the app; after the app has shut down, Velopack's updater replaces the program
    ///     files and starts the new version (<see cref="StartPendingApply" />). The data folder is never touched.
    /// </summary>
    public static void ApplyAndRestart()
    {
        if (Status.State != UpdateState.ReadyToRestart || _velopack == null || _pendingUpdate == null) return;
        var target = _pendingUpdate.TargetFullRelease;
        var attempt = new ApplyAttempt
        {
            FromVersion = InstalledVersionText,
            ToVersion = target.Version.ToString(),
            Channel = Channel,
            ManualUrl = Status.ManualUrl,
            ReleasePageUrl = Status.ReleasePageUrl,
            StartedAt = DateTimeOffset.Now
        };
        try
        {
            CheckPreconditions(target.Size, true);
            if (Locator?.UpdateExePath is not { } updateExe || !File.Exists(updateExe))
            {
                throw new UpdateException(UpdateFailureKind.ApplyFailed, "The updater (Update.exe) is missing");
            }

            attempt.Save();
        }
        catch (Exception ex)
        {
            var kind = Classify(ex);
            Shell.Logger.Error(ex, "Can't start the update installation ({kind})", kind);
            ApplyAttempt.Take();
            SetFailed(kind, ex.Message, null, null);
            return;
        }

        Shell.Logger.Info("Installing update {from} -> {to}: closing the app", attempt.FromVersion, attempt.ToVersion);
        _applyOnExit = target;
        AppHost.Shutdown();
        // Normally Program.Main starts the updater once the app lifetime has ended; exit even if something keeps it alive
        _ = Task.Delay(SHUTDOWN_FALLBACK_MS).ContinueWith(_ =>
        {
            StartPendingApply();
            Environment.Exit(0);
        }, TaskScheduler.Default);
    }

    /// <summary>
    ///     Called by Program.Main after the application lifetime ended: starts the updater prepared by
    ///     <see cref="ApplyAndRestart" />, which waits for this process to exit. Starting it only after the shutdown means
    ///     settings, logs and the tray icon are closed normally and the updater never races the closing app.
    ///     A failure here is reported on the next start (<see cref="ApplyAttempt" />).
    /// </summary>
    public static void StartPendingApply()
    {
        var target = _applyOnExit;
        if (target == null || _velopack == null || Interlocked.Exchange(ref _applyStarted, 1) == 1) return;
        try
        {
            _velopack.WaitExitThenApplyUpdates(target, silent: false, restart: true);
            Shell.Logger.Info("Updater started for {version}", target.Version);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Can't start the updater");
        }

        LogManager.Flush();
    }

    /// <summary>
    ///     Retries the step that failed: a new check (the download starts from a fresh check as well).
    /// </summary>
    public static void Retry()
    {
        Status.State = UpdateState.Idle;
        CheckForUpdates(true);
    }

    /// <summary>
    ///     Throws <see cref="UpdateException" /> when the update cannot be downloaded / installed by this user now.
    /// </summary>
    private static void CheckPreconditions(long packageSize, bool installing)
    {
        var locator = Locator ?? throw new UpdateException(UpdateFailureKind.Unknown, "Not installed");
        foreach (var folder in GetFoldersToWrite(locator))
        {
            if (!DataRoot.IsWritable(folder, out var error))
            {
                throw new UpdateException(UpdateFailureKind.NoWriteAccess, folder, error);
            }
        }

        var root = locator.RootAppDir ?? AppContext.BaseDirectory;
        var required = (long) (packageSize * REQUIRED_SPACE_FACTOR) + REQUIRED_SPACE_MARGIN;
        foreach (var folder in new[] {root, locator.PackagesDir}.OfType<string>())
        {
            var free = GetFreeSpace(folder);
            if (free >= 0 && free < required)
            {
                throw new UpdateException(UpdateFailureKind.NoSpace,
                    $"{folder}: {free / 1048576} MB free, {required / 1048576} MB required");
            }
        }

        if (installing && FindOtherInstances(root) is {Length: > 0} others)
        {
            throw new UpdateException(UpdateFailureKind.Busy,
                "Other NebulaAuth processes use this folder: " + string.Join(", ", others));
        }
    }

    private static string[] GetFoldersToWrite(IVelopackLocator locator)
    {
        var folders = new System.Collections.Generic.List<string>();
        if (locator.PackagesDir is { } packages) folders.Add(packages);
        // Checked before the download as well: no point in downloading what can't be installed without elevation
        if (locator.RootAppDir is { } root)
        {
            // Windows replaces "current" inside the root; macOS replaces the .app bundle, Linux the AppImage file:
            // both need write access to the folder that contains them
            folders.Add(OperatingSystem.IsWindows() ? root : Path.GetDirectoryName(root) ?? root);
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static long GetFreeSpace(string folder)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(drive) ? -1 : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string[] FindOtherInstances(string root)
    {
        var self = Environment.ProcessId;
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var result = new System.Collections.Generic.List<string>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath)
                                                           ?? "NebulaAuth"))
        {
            using (process)
            {
                if (process.Id == self) continue;
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path != null && path.StartsWith(rootPrefix, comparison))
                        result.Add($"{process.Id}");
                }
                catch (Exception)
                {
                    // A process of another user / elevated: not ours to update
                }
            }
        }

        return result.ToArray();
    }

    private static void SetFailed(UpdateFailureKind kind, string? detail, string? manualUrl, string? releasePageUrl)
    {
        Status.FailureKind = kind;
        Status.FailureDetail = detail;
        if (manualUrl != null) Status.ManualUrl = manualUrl;
        if (releasePageUrl != null) Status.ReleasePageUrl = releasePageUrl;
        Status.ManualUrl ??= UpdateTrust.RELEASES_PAGE_URL;
        Status.State = UpdateState.Failed;
    }

    public static UpdateFailureKind Classify(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case UpdateException update:
                    return update.Kind;
                case ChecksumFailedException:
                    return UpdateFailureKind.Checksum;
                case AcquireLockFailedException:
                    return UpdateFailureKind.Busy;
                case UnauthorizedAccessException:
                    return UpdateFailureKind.NoWriteAccess;
                case IOException io when IsDiskFull(io):
                    return UpdateFailureKind.NoSpace;
                case HttpRequestException or HttpIOException or System.Net.Sockets.SocketException
                    or TaskCanceledException or TimeoutException:
                    return UpdateFailureKind.Network;
            }
        }

        return UpdateFailureKind.Unknown;
    }

    private static bool IsDiskFull(IOException ex)
    {
        // ERROR_HANDLE_DISK_FULL (0x27), ERROR_DISK_FULL (0x70); ENOSPC (28) on Unix
        var code = ex.HResult & 0xFFFF;
        return code is 0x27 or 0x70 || (!OperatingSystem.IsWindows() && code == 28);
    }

    private static SignedFeedSource CreateSource()
    {
        var baseUrl = UpdateTrust.FEED_BASE_URL;
        var key = UpdateTrust.FEED_PUBLIC_KEY_PEM;
        var allowHttp = false;
#if DEBUG
        ApplyDebugFeedOverrides(ref baseUrl, ref key, ref allowHttp);
#endif
        if (!SignedFeedSource.IsKeyConfigured(key))
        {
            throw new UpdateException(UpdateFailureKind.Signature,
                "No update signing key is configured in this build: the feed can't be verified");
        }

        return new SignedFeedSource(baseUrl, key, allowHttp);
    }

    private static async Task LoadChangelogAsync(string version)
    {
        Status.Changelog = null;
        Status.ChangelogUrl = null;
        try
        {
            var response = await ChangelogHttp.GetAsync($"{UpdateTrust.CHANGELOG_BASE_URL}{version}.json");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                Status.Changelog = JsonConvert.DeserializeObject<ChangelogEntry>(json);
            }
        }
        catch (Exception ex)
        {
            Shell.Logger.Debug(ex, "Changelog {version} not loaded", version);
        }

        if (Status.Changelog == null) Status.ChangelogUrl = Status.ReleasePageUrl;
    }

    /// <summary>
    ///     Velopack diagnostics go to the app log (data folder).
    /// </summary>
    private sealed class NLogVelopackLogger : IVelopackLogger
    {
        private static readonly Logger Target = LogManager.GetLogger("Velopack");

        public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
        {
            var level = logLevel switch
            {
                VelopackLogLevel.Trace => LogLevel.Trace,
                VelopackLogLevel.Debug => LogLevel.Debug,
                VelopackLogLevel.Information => LogLevel.Info,
                VelopackLogLevel.Warning => LogLevel.Warn,
                VelopackLogLevel.Error => LogLevel.Error,
                _ => LogLevel.Fatal
            };
            Target.Log(level, exception, message);
        }
    }
}
