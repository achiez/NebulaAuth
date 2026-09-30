using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using NebulaAuth.Model;

namespace NebulaAuth.Core;

public static class TrayManager
{
    private static TrayIcon? _trayIcon;
    private static Window? _mainWindow;
    private static bool IsEnabled => Settings.Instance.HideToTray;

    /// <summary>
    ///     Whether minimizing may hide the window (no taskbar entry) and leave only the tray icon.
    ///     Windows (notification area) and macOS (menu bar) always show the icon. Linux has no guaranteed tray: without a
    ///     StatusNotifierItem host (stock GNOME) the icon is invisible and Avalonia cannot tell whether a host exists, so
    ///     a hidden window could not be restored. There the window only stays minimized (the tray icon is still shown
    ///     for desktops that have a tray).
    /// </summary>
    private static bool CanHideWindow => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    [MemberNotNullWhen(true, nameof(_trayIcon), nameof(_mainWindow))]
    private static bool Init { get; set; }

    public static void InitializeTray(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _trayIcon = new TrayIcon
        {
            ToolTipText = "NebulaAuth",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NebulaAuth/Theme/lock.ico"))),
            IsVisible = false
        };

        // Win32 (and some Linux DEs) raise Clicked on a single click
        _trayIcon.Clicked += TrayIcon_Clicked;

        var contextMenu = new NativeMenu();
        if (!OperatingSystem.IsWindows())
        {
            // macOS (and some Linux DEs) never raise Clicked: the menu is the only way to restore the window there
            var showItem = new NativeMenuItem("Show");
            showItem.Click += (_, _) => ShowMainWindow();
            contextMenu.Items.Add(showItem);
        }

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += OnExitClick;
        contextMenu.Items.Add(exitItem);
        _trayIcon.Menu = contextMenu;

        // Registering the icon in the application makes Avalonia remove it on exit
        if (Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
            icons.Add(_trayIcon);
            TrayIcon.SetIcons(app, icons);

            // macOS: a click on the Dock icon while the window is hidden asks the app to reopen
            if (app.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += Application_Activated;
            }
        }

        mainWindow.PropertyChanged += MainWindow_PropertyChanged;
        Init = true;
    }

    private static void OnExitClick(object? sender, EventArgs e)
    {
        ExitApplication();
    }

    private static void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private static void Application_Activated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Reopen && _mainWindow is {IsVisible: false})
        {
            ShowMainWindow();
        }
    }

    private static void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        if (_mainWindow?.WindowState == WindowState.Minimized)
        {
            HideMainWindow();
        }
        else if (Init && _mainWindow.IsVisible)
        {
            // Restored without the tray icon (taskbar on Linux, where the window is only minimized)
            _trayIcon.IsVisible = false;
        }
    }

    private static void ShowMainWindow()
    {
        if (!Init) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _trayIcon.IsVisible = false;
    }

    private static void HideMainWindow()
    {
        if (!Init) return;
        if (!IsEnabled) return;
        _trayIcon.IsVisible = true;
        if (CanHideWindow)
        {
            _mainWindow.Hide();
        }
    }

    private static void ExitApplication()
    {
        if (!Init) return;
        if (StorageOperationLock.IsBusy)
        {
            ShowMainWindow();
            SnackbarController.SendSnackbar(StorageOperationLock.BusyMessage);
            return;
        }

        // The icon is registered in TrayIcon.Icons of the application, Avalonia disposes it on exit
        _trayIcon.IsVisible = false;
        AppHost.Shutdown();
    }
}
