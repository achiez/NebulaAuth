using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NebulaAuth.Core;
using NebulaAuth.Utility;
using Newtonsoft.Json;

namespace NebulaAuth.Model;

public partial class Settings : ObservableObject
{
    public static Settings Instance { get; }

    private static IReadOnlyDictionary<ThemeType, string> ThemeNames { get; } = new Dictionary<ThemeType, string>
    {
        {ThemeType.Default, "DefaultTheme"},
        {ThemeType.Light, "LightTheme"},
        {ThemeType.Black, "BlackTheme"},
        {ThemeType.Luxury, "LuxuryTheme"},
        {ThemeType.Shadcn, "ShadcnTheme"}
    };

    static Settings()
    {
        if (!File.Exists("settings.json"))
        {
            Instance = new Settings();
            Instance.PropertyChanged += SettingsOnPropertyChanged;
            try
            {
                Instance.Language = LanguageUtility.DetectPreferredLanguage();
            }
            catch (Exception)
            {
                // A failure here (culture lookup, first save) would break the static ctor and the app could not start:
                // keep the default language
            }

            return;
        }

        try
        {
            var json = File.ReadAllText("settings.json");
            var settings = JsonConvert.DeserializeObject<Settings>(json) ?? throw new NullReferenceException();
            Instance = settings;
        }
        catch (Exception ex)
        {
            SnackbarController.SendSnackbar(LocManager.GetCodeBehindOrDefault("Error when loading settings", "Settings",
                "ErrorWhileLoadingSettings"));
            SnackbarController.SendSnackbar(ex.Message);
            Instance = new Settings();
        }

        Instance.PropertyChanged += SettingsOnPropertyChanged;
    }


    private static void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Save();
        if (e.PropertyName == nameof(ThemeType))
        {
            ThemeManager.ApplyTheme(ThemeNames[Instance.ThemeType]);
        }
    }

    public static void Save()
    {
        var json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
        File.WriteAllText("settings.json", json);
    }

    public static void ResetThemeDefaults()
    {
        Instance.BackgroundBlur = 0.0;
        Instance.BackgroundOpacity = 1.0;
        Instance.BackgroundGamma = 0.0;
        Instance.LeftOpacity = 0.4;
        Instance.RightOpacity = 0.4;
        Instance.ApplyBlurBackground = true;
        Instance.RippleDisabled = false;
        Save();
    }

    public string GetTheme()
    {
        return ThemeNames.TryGetValue(ThemeType, out var themeName) ? themeName : ThemeNames[ThemeType.Default];
    }


    #region Properties

    [ObservableProperty] private bool _hideToTray;
    [ObservableProperty] private int _timerSeconds = 60;
    // Persisted as "#AARRGGBB" (the format of existing settings.json files), null when unset
    [ObservableProperty] [property: JsonConverter(typeof(HexColorJsonConverter))]
    private Color? _iconColor;
    [ObservableProperty] private bool _isPasswordSet;
    [ObservableProperty] private LocalizationLanguage _language = LocalizationLanguage.English;
    [ObservableProperty] private bool _confirmMafileImport = true;
    [ObservableProperty] private bool _allowAutoUpdate;
    [ObservableProperty] private bool _useAccountNameAsMafileName;

    [ObservableProperty] private BackgroundMode _backgroundMode = BackgroundMode.Default;
    [ObservableProperty] private double _leftOpacity = 0.4;
    [ObservableProperty] private double _rightOpacity = 0.4;
    [ObservableProperty] private double _backgroundBlur;
    [ObservableProperty] private double _backgroundOpacity = 1;
    [ObservableProperty] private double _backgroundGamma;
    [ObservableProperty] private bool _applyBlurBackground = true;
    [ObservableProperty] private ThemeType _themeType = ThemeType.Default;
    [ObservableProperty] private bool _rippleDisabled;
    [ObservableProperty] private bool _proxyManagerDisplayProtocol;
    [ObservableProperty] private bool _proxyManagerDisplayCredentials;

    [ObservableProperty] private TimeSpan _maacErrorThreshold = TimeSpan.FromHours(3);
    [ObservableProperty] private TimeSpan _maacRetryInterval = TimeSpan.FromMinutes(15);

    #endregion
}

public enum BackgroundMode
{
    Default,
    Custom,
    Color
}

public enum ThemeType
{
    Default = 0,
    Black = 1,
    Light = 2,
    Luxury = 3,
    Shadcn = 4
}