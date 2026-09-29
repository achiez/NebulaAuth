using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace NebulaAuth.Core;

/// <summary>
///     Access to the application main window / top level and application shutdown.
/// </summary>
public static class AppHost
{
    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <summary>
    ///     The application main window, or null before it is created (splash / startup) or when not running
    ///     with a desktop lifetime.
    /// </summary>
    public static Window? MainWindow => Desktop?.MainWindow;

    /// <summary>
    ///     The top level to use for platform services (clipboard, storage provider, launcher).
    ///     Main window if it exists, otherwise the first open window (e.g. splash during startup).
    /// </summary>
    public static TopLevel? TopLevel
    {
        get
        {
            var desktop = Desktop;
            if (desktop == null) return null;
            if (desktop.MainWindow != null) return desktop.MainWindow;
            return desktop.Windows.Count > 0 ? desktop.Windows[0] : null;
        }
    }

    /// <summary>
    ///     True when the main window is minimized (or hidden to tray). False if there is no main window.
    /// </summary>
    public static bool IsMainWindowMinimized => MainWindow is {WindowState: WindowState.Minimized};

    /// <summary>
    ///     Registers <paramref name="window" /> as the application main window.
    /// </summary>
    public static void RegisterMainWindow(Window window)
    {
        if (Desktop is { } desktop)
        {
            desktop.MainWindow = window;
        }
    }

    /// <summary>
    ///     Shuts the application down with the given exit code. Safe to call from any thread.
    /// </summary>
    public static void Shutdown(int exitCode = 0)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Shutdown(exitCode));
            return;
        }

        Desktop?.Shutdown(exitCode);
    }
}
