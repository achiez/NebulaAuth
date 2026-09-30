using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles;

namespace NebulaAuth.ViewModel.Other;

public partial class SettingsVM : ObservableObject
{
    public const int MAIN_TAB = 0;
    public const int ENCRYPTION_TAB = 2;
    public const int DEVICES_TAB = 3;

    public Settings Settings => Settings.Instance;

    [ObservableProperty] private double _renameMafilesProgress;

    [ObservableProperty] private string? _renameResultText;

    [ObservableProperty] private int _selectedTab;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyRenameSettingCommand))]
    private bool _useAccountNameAsMafileNamePreview;

    public SettingsVM()
    {
        _useAccountNameAsMafileNamePreview = Settings.UseAccountNameAsMafileName;
    }

    /// <summary>
    ///     Third tab: storage mode and the encryption operations.
    /// </summary>
    public EncryptionSettingsVM Encryption { get; } = new();

    /// <summary>
    ///     Fourth tab: phone models used as the device names of the accounts.
    /// </summary>
    public DeviceModelsSettingsVM Devices { get; } = new();

    [RelayCommand]
    private void ResetThemeDefaults()
    {
        Settings.ResetThemeDefaults();
        OnPropertyChanged(nameof(BackgroundBlur));
        OnPropertyChanged(nameof(BackgroundOpacity));
        OnPropertyChanged(nameof(BackgroundGamma));
        OnPropertyChanged(nameof(LeftOpacity));
        OnPropertyChanged(nameof(RightOpacity));
        OnPropertyChanged(nameof(ApplyBlurBackground));
        OnPropertyChanged(nameof(RippleDisabled));
    }

    [RelayCommand(CanExecute = nameof(ApplyRenameSettingCanExecute))]
    private async Task ApplyRenameSetting()
    {
        RenameResultText = null;
        var targetValue = UseAccountNameAsMafileNamePreview;
        if (UseAccountNameAsMafileName == targetValue) return;
        RenameMafilesProgress = 0;
        MafilesBulkRenameResult? result = null;
        try
        {
            result = await Storage.RenameMafiles(targetValue, new Progress<double>(p => RenameMafilesProgress = p));
            UseAccountNameAsMafileName = targetValue;
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Error while renaming mafiles");
        }
        finally
        {
            RenameMafilesProgress = 0;
        }

        if (result == null)
        {
            RenameResultText = GetLoc("Error");
            return;
        }

        if (result.Total == 0) return;

        if (result.NotRenamed == 0)
        {
            var l = GetLoc("AllRenamed");
            RenameResultText = string.Format(l, result.Total, result.BackupFileName);
        }
        else
        {
            var l = GetLoc("PartialSuccess");
            RenameResultText =
                string.Format(l, result.Total, result.Renamed, result.Conflict, result.Errors, result.BackupFileName);
        }

        string GetLoc(string key)
        {
            return LocManager.GetCodeBehindOrDefault(key, "SettingsVM", "MafileRenaming", key);
        }
    }

    private bool ApplyRenameSettingCanExecute()
    {
        return UseAccountNameAsMafileNamePreview != UseAccountNameAsMafileName;
    }

    #region SettingsProps

    public bool HideToTray
    {
        get => Settings.HideToTray;
        set => Settings.HideToTray = value;
    }

    public Dictionary<BackgroundMode, string> BackgroundModes => new()
    {
        {BackgroundMode.Default, LocManager.GetOrDefault("Default", "SettingsDialog", "BackgroundMode", "Default")},
        {BackgroundMode.Custom, LocManager.GetOrDefault("Default", "SettingsDialog", "BackgroundMode", "Custom")},
        {BackgroundMode.Color, LocManager.GetOrDefault("Default", "SettingsDialog", "BackgroundMode", "NoBackground")}
    };

    public Dictionary<ThemeType, string> ThemeTypes => new()
    {
        {ThemeType.Default, "Default"},
        {ThemeType.Black, "Black"},
        {ThemeType.Light, "Light"},
        {ThemeType.Luxury, "Luxury"},
        {ThemeType.Shadcn, "Shadcn"}
    };

    public Dictionary<LocalizationLanguage, string> Languages { get; } = new()
    {
        {LocalizationLanguage.English, "English"},
        {LocalizationLanguage.Russian, "Русский"},
        {LocalizationLanguage.Ukrainian, "Українська"},
        {LocalizationLanguage.ChineseSimplified, "简体中文"},
        {LocalizationLanguage.French, "Français"},
        {LocalizationLanguage.Spanish, "Español"},
        {LocalizationLanguage.Turkish, "Türkçe"},
        {LocalizationLanguage.Kazakh, "Қазақша"}
    };

    public Color? IconColor
    {
        get => Settings.IconColor;
        set
        {
            // The color picker (non-nullable Color) stays bound while the indicator is off:
            // only UseIcon may turn the indicator on
            if (value != null && !UseIcon) return;
            Settings.IconColor = value;
        }
    }

    public bool UseIcon
    {
        get => IconColor != null;
        set
        {
            if (!value)
                Settings.IconColor = null;
            else
                Settings.IconColor = Color.FromRgb(202, 39, 39);

            OnPropertyChanged();
            OnPropertyChanged(nameof(IconColor));
        }
    }


    public LocalizationLanguage Language
    {
        get => Settings.Language;
        set
        {
            Settings.Language = value;
            LocManager.SetApplicationLocalization(value);
        }
    }

    public bool ConfirmMafileImport
    {
        get => Settings.ConfirmMafileImport;
        set => Settings.ConfirmMafileImport = value;
    }

    public bool AllowAutoUpdate
    {
        get => Settings.AllowAutoUpdate;
        set => Settings.AllowAutoUpdate = value;
    }

    public bool UseAccountNameAsMafileName
    {
        get => Settings.UseAccountNameAsMafileName;
        set
        {
            Settings.UseAccountNameAsMafileName = value;
            ApplyRenameSettingCommand.NotifyCanExecuteChanged();
        }
    }

    #endregion

    #region Theme

    public BackgroundMode BackgroundMode
    {
        get => Settings.BackgroundMode;
        set => Settings.BackgroundMode = value;
    }

    public double BackgroundBlur
    {
        get => Settings.BackgroundBlur;
        set => Settings.BackgroundBlur = value;
    }

    public double BackgroundOpacity
    {
        get => Settings.BackgroundOpacity;
        set => Settings.BackgroundOpacity = value;
    }

    public double BackgroundGamma
    {
        get => Settings.BackgroundGamma;
        set => Settings.BackgroundGamma = value;
    }

    public double LeftOpacity
    {
        get => Settings.LeftOpacity;
        set => Settings.LeftOpacity = value;
    }

    public double RightOpacity
    {
        get => Settings.RightOpacity;
        set => Settings.RightOpacity = value;
    }

    public bool ApplyBlurBackground
    {
        get => Settings.ApplyBlurBackground;
        set => Settings.ApplyBlurBackground = value;
    }

    public ThemeType ThemeType
    {
        get => Settings.ThemeType;
        set => Settings.ThemeType = value;
    }

    public bool RippleDisabled
    {
        get => Settings.RippleDisabled;
        set => Settings.RippleDisabled = value;
    }

    #endregion
}