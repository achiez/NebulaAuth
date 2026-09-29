#if DEBUG
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Model.Update;
using Velopack.Locators;

namespace NebulaAuth.Core;

/// <summary>
///     Debug builds only: drive the update flow of a packaged debug build from test scripts, without UI automation.
///     <list type="bullet">
///         <item><c>NEBULA_UPDATE_FEED</c>: feed base URL instead of the repository (http allowed).</item>
///         <item><c>NEBULA_UPDATE_PUBKEY</c>: path of a PEM public key used instead of the pinned one (test key).</item>
///         <item><c>NEBULA_UPDATE_AUTO</c>=download|apply: presses "Download and install" once the dialog shows an update,
///             and with "apply" also "Restart and update" when the download is ready. Nothing is pressed after a failed
///             installation was reported on this start.</item>
///         <item><c>NEBULA_DIAG_REPORT</c>=&lt;ms&gt;: writes diag-report-&lt;pid&gt;.txt (version, folders, loaded accounts,
///             settings, update state, modules loaded from outside the program folder) to the data folder after that
///             delay; with <c>NEBULA_DIAG_EXIT=1</c> the app then exits.</item>
///     </list>
/// </summary>
public static partial class UpdateManager
{
    private static bool _autoDownloadStarted;
    private static bool _autoApplyStarted;
    private static bool _failureReportedThisRun;

    private static void ApplyDebugFeedOverrides(ref string baseUrl, ref string key, ref bool allowHttp)
    {
        var feed = Environment.GetEnvironmentVariable("NEBULA_UPDATE_FEED");
        if (!string.IsNullOrWhiteSpace(feed))
        {
            baseUrl = feed.Trim();
            allowHttp = true;
        }

        var keyFile = Environment.GetEnvironmentVariable("NEBULA_UPDATE_PUBKEY");
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            key = File.ReadAllText(keyFile.Trim());
        }
    }

    /// <summary>
    ///     <c>NEBULA_UPDATE_MANUAL=1</c>: the automatic startup check behaves like "Check for updates" (a failure opens
    ///     the dialog instead of only being logged).
    /// </summary>
    static partial void AdjustDebugCheck(ref bool manual)
    {
        if (Environment.GetEnvironmentVariable("NEBULA_UPDATE_MANUAL") == "1") manual = true;
    }

    static partial void StartDebugHooks()
    {
        var auto = Environment.GetEnvironmentVariable("NEBULA_UPDATE_AUTO")?.Trim().ToLowerInvariant();
        if (auto is "download" or "apply")
        {
            Status.PropertyChanged += (_, e) => OnDebugStatusChanged(e, auto);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("NEBULA_DIAG_REPORT"), out var delay))
        {
            _ = WriteDiagReportAsync(delay);
        }
    }

    private static void OnDebugStatusChanged(PropertyChangedEventArgs e, string auto)
    {
        if (e.PropertyName != nameof(UpdateStatus.State)) return;
        switch (Status.State)
        {
            case UpdateState.Failed:
                _failureReportedThisRun = true;
                break;
            case UpdateState.Available when !_autoDownloadStarted && !_failureReportedThisRun:
                _autoDownloadStarted = true;
                Shell.Logger.Info("[debug] NEBULA_UPDATE_AUTO: starting the download");
                DispatcherTimer.RunOnce(StartDownload, TimeSpan.FromSeconds(2));
                break;
            case UpdateState.ReadyToRestart when auto == "apply" && !_autoApplyStarted && !_failureReportedThisRun:
                _autoApplyStarted = true;
                Shell.Logger.Info("[debug] NEBULA_UPDATE_AUTO: installing");
                DispatcherTimer.RunOnce(ApplyAndRestart, TimeSpan.FromSeconds(2));
                break;
        }
    }

    private static async Task WriteDiagReportAsync(int delay)
    {
        await Task.Delay(delay);
        var sb = new StringBuilder();
        try
        {
            var locator = VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null;
            var baseDir = AppContext.BaseDirectory;
            sb.AppendLine($"version={InstalledVersionText} asm={Assembly.GetEntryAssembly()?.GetName().Version}");
            sb.AppendLine($"velopack installed={locator?.CurrentlyInstalledVersion} channel={locator?.Channel} " +
                          $"root={locator?.RootAppDir} portable={locator?.IsPortable} packages={locator?.PackagesDir}");
            sb.AppendLine($"exe={Environment.ProcessPath}");
            sb.AppendLine($"baseDir={baseDir}");
            sb.AppendLine($"cwd={Environment.CurrentDirectory}");
            sb.AppendLine($"dataRoot={DataRoot.Path} source={DataRoot.Source}");
            sb.AppendLine($"mafilesLoaded={Storage.MaFiles.Count} mafilesDir={Storage.MafilesDirectory}");
            sb.AppendLine($"settings.language={Settings.Instance.Language} theme={Settings.Instance.ThemeType} " +
                          $"timer={Settings.Instance.TimerSeconds}");
            await Dispatcher.UIThread.InvokeAsync(() =>
                sb.AppendLine($"update.state={Status.State} failure={Status.FailureKind} available={Status.AvailableVersion} " +
                              $"canInstall={Status.CanInstall} manualUrl={Status.ManualUrl} detail={Status.FailureDetail}"));
            sb.AppendLine("--- managed assemblies outside baseDir:");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var location = assembly.IsDynamic ? "" : assembly.Location;
                if (!string.IsNullOrEmpty(location) &&
                    !location.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("  " + location);
            }

            if (OperatingSystem.IsWindows())
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName).ToList();
                sb.AppendLine("--- native modules outside baseDir and the Windows folder:");
                foreach (var module in modules.Where(m =>
                             !m.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) &&
                             !m.StartsWith(windows, StringComparison.OrdinalIgnoreCase)))
                    sb.AppendLine("  " + module);
                sb.AppendLine($"--- modules total={modules.Count} underBaseDir=" +
                              modules.Count(m => m.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("REPORT ERROR " + ex);
        }

        var file = DataRoot.Combine($"diag-report-{Environment.ProcessId}.txt");
        await File.WriteAllTextAsync(file, sb.ToString());
        Shell.Logger.Info("[debug] diagnostic report written: {file}", file);
        if (Environment.GetEnvironmentVariable("NEBULA_DIAG_EXIT") == "1") AppHost.Shutdown();
    }
}
#endif
