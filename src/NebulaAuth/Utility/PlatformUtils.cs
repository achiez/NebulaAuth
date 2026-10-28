using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NebulaAuth.Core;
using NLog;

namespace NebulaAuth.Utility;

/// <summary>
///     Cross-platform shell helpers (replacement for <c>Process.Start(url){UseShellExecute=true}</c> and
///     <c>Process.Start("explorer.exe", path)</c>). They never throw and can be called from any thread: the launch runs
///     asynchronously, a failure is logged and reported with a snackbar that shows the URL / path and offers to copy it.
/// </summary>
/// <remarks>
///     Windows uses Avalonia's <see cref="ILauncher" /> (<c>TopLevel.Launcher</c>) with the shell as fallback.
///     On macOS / Linux Avalonia 12's launcher (<c>BclLauncher</c> in the Native and X11 backends) only starts
///     <c>open</c> / <c>xdg-open</c> and reports success without waiting for them, so a missing browser or file manager
///     would fail silently. There the opener is run directly and its exit code is checked (Linux also tries
///     <c>gio open</c>). The launcher is deliberately not used as a fallback there: it would run the same opener that just
///     failed and report success, which would hide the failure snackbar.
/// </remarks>
public static class PlatformUtils
{
    // An opener that is still running after this time is considered successful (the generic xdg-open fallback runs the
    // browser in the foreground); failures (no handler, tool missing) exit immediately with a non-zero code
    private static readonly TimeSpan OpenerExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureSnackbarDuration = TimeSpan.FromSeconds(8);
    private static readonly Logger Logger = LogManager.GetLogger("Logger");

    /// <summary>
    ///     Opens <paramref name="url" /> in the default browser.
    /// </summary>
    public static void OpenUrl(string url)
    {
        _ = RunAsync(url, () => OpenUrlAsync(url));
    }

    /// <summary>
    ///     Opens <paramref name="path" /> (a directory) in the system file manager.
    /// </summary>
    public static void OpenFolder(string path)
    {
        if (TryGetFullPath(path) is not { } fullPath) return;
        _ = RunAsync(fullPath, () => OpenFolderAsync(fullPath));
    }

    /// <summary>
    ///     Opens the file manager with <paramref name="filePath" /> selected (Windows: <c>explorer /select</c>,
    ///     macOS: <c>open -R</c>). On Linux there is no universal "select" command, the containing folder is opened
    ///     (also the fallback on the other systems).
    /// </summary>
    public static void RevealFile(string filePath)
    {
        if (TryGetFullPath(filePath) is not { } fullPath) return;
        _ = RunAsync(fullPath, () => RevealFileAsync(fullPath));
    }

    private static async Task<bool> OpenUrlAsync(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            var uri = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null;
            return (uri != null && await TryLauncherAsync(l => l.LaunchUriAsync(uri)))
                   || TryStart(new ProcessStartInfo(url) {UseShellExecute = true});
        }

        // No launcher fallback here (see remarks): it would report success after the opener failed
        return await TryOpenersAsync(url);
    }

    private static async Task<bool> OpenFolderAsync(string fullPath)
    {
        if (!Directory.Exists(fullPath))
        {
            Logger.Warn("Can't open folder {path}: it does not exist", fullPath);
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return await TryLauncherAsync(l => l.LaunchDirectoryInfoAsync(new DirectoryInfo(fullPath)))
                   || TryStart(new ProcessStartInfo("explorer.exe") {ArgumentList = {fullPath}});
        }

        // No launcher fallback here (see remarks): it would report success after the opener failed
        return await TryOpenersAsync(fullPath);
    }

    private static async Task<bool> RevealFileAsync(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // explorer.exe /select, "path"
            if (TryStart(new ProcessStartInfo("explorer.exe", $"/select, \"{fullPath}\""))) return true;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (await RunOpenerAsync("open", "-R", fullPath)) return true;
        }

        return await OpenFolderAsync(Path.GetDirectoryName(fullPath) ?? fullPath);
    }

    private static async Task RunAsync(string target, Func<Task<bool>> open)
    {
        try
        {
            if (await open()) return;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Can't open {target}", target);
        }

        ReportFailure(target);
    }

    // macOS: open. Linux and others: xdg-open, then gio (GLib) when xdg-utils is missing
    private static async Task<bool> TryOpenersAsync(string target)
    {
        if (OperatingSystem.IsMacOS())
        {
            return await RunOpenerAsync("open", target);
        }

        return await RunOpenerAsync("xdg-open", target) || await RunOpenerAsync("gio", "open", target);
    }

    private static async Task<bool> RunOpenerAsync(string fileName, params string[] args)
    {
        Process? process;
        try
        {
            var psi = new ProcessStartInfo(fileName) {UseShellExecute = false};
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Not installed or not allowed (sandbox)
            Logger.Warn(ex, "Can't start {opener}", fileName);
            return false;
        }

        if (process == null) return false;
        using (process)
        {
            using var cts = new CancellationTokenSource(OpenerExitTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return true;
            }

            if (process.ExitCode == 0) return true;
            Logger.Warn("{opener} exited with code {code}", fileName, process.ExitCode);
            return false;
        }
    }

    private static bool TryStart(ProcessStartInfo psi)
    {
        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Can't start {file}", psi.FileName);
            return false;
        }
    }

    // Avalonia launcher of the main window (UI thread); false when unavailable or failed. Windows only (see remarks)
    private static async Task<bool> TryLauncherAsync(Func<ILauncher, Task<bool>> launch)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var launcher = AppHost.TopLevel?.Launcher;
                return launcher != null && await launch(launcher);
            });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Avalonia launcher failed");
            return false;
        }
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Invalid path {path}", path);
            ReportFailure(path);
            return null;
        }
    }

    // "Error: <url or path>" with a "Copy" button, so the user can open it manually
    private static void ReportFailure(string target)
    {
        try
        {
            Logger.Warn("Can't open {target}", target);
            var text = $"{LocManager.GetCommonOrDefault("Error", "Error")}: {target}";
            var copyText = LocManager.GetOrDefault("Copy", "MainWindow", "ContextMenus", "Proxy", "Copy");
            SnackbarController.SendSnackbarWithButton(text, copyText, () => CopyToClipboard(target),
                FailureSnackbarDuration);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Can't report the failure to open {target}", target);
        }
    }

    private static void CopyToClipboard(string text)
    {
        if (ClipboardHelper.Set(text))
        {
            SnackbarController.SendSnackbar(LocManager.GetOrDefault("Copied", "MainWindow", "CodeCopied"));
        }
    }
}
