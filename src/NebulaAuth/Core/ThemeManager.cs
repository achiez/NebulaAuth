using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using NebulaAuth.Model;
using NebulaAuth.Theme;

namespace NebulaAuth.Core;

public static class ThemeManager
{
    private static bool _settingsSubscribed;

    /// <summary>
    ///     Name of the applied theme ("DefaultTheme", "BlackTheme", "LightTheme", "LuxuryTheme", "ShadcnTheme").
    /// </summary>
    public static string? CurrentTheme { get; private set; }

    /// <summary>
    ///     The applied palette (null before the first <see cref="ApplyTheme" />).
    /// </summary>
    public static ThemePalette? CurrentPalette { get; private set; }

    private static void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.IconColor))
        {
            UpdateIcon();
        }
    }

    private static void UpdateIcon()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateIcon);
            return;
        }

        // Avalonia has no taskbar overlay API: the colored dot is composed into the window icon itself
        // (taskbar, Alt+Tab, X11 _NET_WM_ICON).
        var window = AppHost.MainWindow;
        if (window == null) return;
        try
        {
            window.Icon = WindowIconComposer.Create(Settings.Instance.IconColor);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Failed to update the window icon");
        }
    }


    public static void ApplyTheme(string themeName)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyTheme(themeName));
            return;
        }

        var app = Application.Current;
        if (app == null) return;

        ThemePalette palette;
        try
        {
            palette = ThemePalette.Load(themeName);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Theme {theme} not found, falling back to DefaultTheme", themeName);
            themeName = "DefaultTheme";
            palette = ThemePalette.Load(themeName);
        }

        var variant = ThemeResources.VariantFor(palette);
        ThemeResources.Apply(app, palette, variant);
        app.RequestedThemeVariant = variant;
        CurrentTheme = themeName;
        CurrentPalette = palette;

        if (!ThemeResources.VerifySemiMapping(app, palette, variant))
        {
            Shell.Logger.Warn("Semi control tokens do not follow the palette of {theme}", themeName);
        }
    }


    /// <summary>
    ///     Applies the theme from <see cref="Settings" /> and follows the icon color. <see cref="ApplyTheme" /> alone does
    ///     not touch the settings, so windows shown before the data folder is known (data folder choice) can use a palette.
    /// </summary>
    public static void InitializeTheme()
    {
        if (!_settingsSubscribed)
        {
            _settingsSubscribed = true;
            Settings.Instance.PropertyChanged += SettingsOnPropertyChanged;
        }

        ApplyTheme(Settings.Instance.GetTheme());
        UpdateIcon();
    }
}
