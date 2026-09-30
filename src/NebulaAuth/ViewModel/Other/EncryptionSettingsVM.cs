using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.Other;

public enum EncryptionPage
{
    Overview,
    Enable,
    Change,
    Disable,
    Skipped,

    /// <summary>Confirmation: turn off saving Steam passwords and delete the saved ones.</summary>
    ForgetPasswords
}

public enum PasswordStrength
{
    None,
    Weak,
    Fair,
    Strong
}

/// <summary>
///     Settings → Encryption: storage mode, skipped / unprotected files and the operations of the storage layer
///     (<see cref="MafileStorageService" />): enable, change the master password, disable, resume an unfinished mode
///     change. While an operation runs the settings dialog can't be closed and the app refuses to exit
///     (<see cref="StorageOperationLock" />). A failure is shown in the tab and never reported as completed.
/// </summary>
public partial class EncryptionSettingsVM : ObservableObject
{
    /// <summary>Minimal length of a new master password (pending product decision: one-line change).</summary>
    public const int MIN_PASSWORD_LENGTH = 8;

    /// <summary>
    ///     Disabling encryption asks for the current master password (pending product decision: one-line change; the
    ///     field and the check are in place).
    /// </summary>
    public const bool DISABLE_REQUIRES_PASSWORD = false;

    private const int RESULT_MIN_DURATION_MS = 4000;

    private readonly MafileStorageService _storage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverviewPage), nameof(IsEnablePage), nameof(IsChangePage),
        nameof(IsDisablePage), nameof(IsSkippedPage), nameof(IsForgetPasswordsPage))]
    private EncryptionPage _page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverviewPage), nameof(IsEnablePage), nameof(IsChangePage),
        nameof(IsDisablePage), nameof(IsSkippedPage), nameof(IsForgetPasswordsPage))]
    private bool _isBusy;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Strength), nameof(StrengthText), nameof(IsWeak),
        nameof(IsFair), nameof(IsStrong))]
    private string _newPassword = string.Empty;

    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private string _currentPassword = string.Empty;
    [ObservableProperty] private string? _formError;

    [ObservableProperty] private string? _progressTitle;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private string? _progressCount;
    [ObservableProperty] private string? _progressFile;

    [ObservableProperty] private SkippedFilesVM _skipped = new([]);

    /// <summary>
    ///     NebulaAuth encrypted files already in the plain storage (another / an earlier key): enabling needs a confirmation.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasEncryptedFiles), nameof(EncryptedFilesText))]
    private int _encryptedFilesCount;

    [ObservableProperty] private bool _confirmEncryptedFiles;

    public EncryptionSettingsVM() : this(MafileStorageService.Current)
    {
    }

    public EncryptionSettingsVM(MafileStorageService storage)
    {
        _storage = storage;
        Refresh();
    }

    #region State

    public StorageMode Mode
    {
        get
        {
            var mode = _storage.Mode;
            DebugOverrideMode(ref mode);
            return mode;
        }
    }

    public bool IsPlain => Mode == StorageMode.Plain;
    public bool IsEncrypted => Mode == StorageMode.Encrypted;
    public bool IsTransition => Mode is StorageMode.Encrypting or StorageMode.Decrypting;

    public bool IsOverviewPage => !IsBusy && Page == EncryptionPage.Overview;
    public bool IsEnablePage => !IsBusy && Page == EncryptionPage.Enable;
    public bool IsChangePage => !IsBusy && Page == EncryptionPage.Change;
    public bool IsDisablePage => !IsBusy && Page == EncryptionPage.Disable;
    public bool IsSkippedPage => !IsBusy && Page == EncryptionPage.Skipped;
    public bool IsForgetPasswordsPage => !IsBusy && Page == EncryptionPage.ForgetPasswords;

    public string ModeTitle => Mode switch
    {
        StorageMode.Encrypted => Loc("Encrypted", "Mode", "Encrypted"),
        StorageMode.Encrypting => Loc("Encrypting…", "Mode", "EncryptingShort"),
        StorageMode.Decrypting => Loc("Decrypting…", "Mode", "DecryptingShort"),
        _ => Loc("Not encrypted", "Mode", "Plain")
    };

    /// <summary>One short line under the mode title; the full explanation is the tooltip (<see cref="ModeDescription" />).</summary>
    public string ModeSubtitle => Mode switch
    {
        StorageMode.Encrypted => Loc("The master password is requested at startup", "Mode", "EncryptedSubtitle"),
        StorageMode.Encrypting or StorageMode.Decrypting => Loc("Interrupted: resume to convert the remaining files",
            "Mode", "TransitionSubtitle"),
        _ => Loc("Mafiles are stored as readable files", "Mode", "PlainSubtitle")
    };

    public string ModeDescription => Mode switch
    {
        StorageMode.Encrypted => Loc(
            "All accepted mafiles and backups are encrypted. The master password is asked on every start. Saved Steam passwords are kept in a separate encrypted file.",
            "Mode", "EncryptedDescription"),
        StorageMode.Encrypting or StorageMode.Decrypting => Loc(
            "Some files are still in the previous format. Resume the operation to finish it; it is also resumed on the next start.",
            "Mode", "TransitionDescription"),
        _ => Loc(
            "Mafiles and backups are stored as readable files: anyone with access to the data folder can use the accounts. Steam passwords can't be saved.",
            "Mode", "PlainDescription")
    };

    public MaterialIconKind ModeIcon => Mode switch
    {
        StorageMode.Encrypted => MaterialIconKind.ShieldLockOutline,
        StorageMode.Encrypting or StorageMode.Decrypting => MaterialIconKind.ShieldSyncOutline,
        _ => MaterialIconKind.ShieldOffOutline
    };

    public int SkippedCount => Skipped.Count;
    public int UnprotectedCount => Skipped.UnprotectedCount;
    public bool HasSkipped => SkippedCount > 0;

    public string SkippedCountText =>
        string.Format(Loc("Skipped files: {0}", "SkippedCount"), SkippedCount);

    public bool HasUnprotected => UnprotectedCount > 0;

    /// <summary>
    ///     The problems row of the status card: "1 file is not protected" (+ "· N files skipped" when other files were
    ///     skipped too) or "N files skipped".
    /// </summary>
    public string ProblemText
    {
        get
        {
            var skipped = PluralText.Get(SkippedCount, "{0} file skipped|{0} files skipped",
                "SettingsDialog", "Encryption", "Problems", "Skipped");
            if (!HasUnprotected) return skipped;
            var text = PluralText.Get(UnprotectedCount, "{0} file is not protected|{0} files are not protected",
                "SettingsDialog", "Encryption", "Problems", "Unprotected");
            return SkippedCount > UnprotectedCount ? text + " · " + skipped : text;
        }
    }

    public bool DisableRequiresPassword => DISABLE_REQUIRES_PASSWORD;

    public bool HasEncryptedFiles => EncryptedFilesCount > 0;

    public string EncryptedFilesText => string.Format(Loc(
        "{0} encrypted file(s) are already in the storage folders. They belong to an earlier or another encryption key: enabling encryption creates a new key that can't open them, so they stay unreadable. To open them instead, restore the storage-state.json that belongs to them (for example from storage-state.json.prev or a backup).",
        "EncryptedFilesWarning"), EncryptedFilesCount);

    private void Refresh()
    {
        Skipped = new SkippedFilesVM(_storage.SkippedFiles);
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsPlain));
        OnPropertyChanged(nameof(IsEncrypted));
        OnPropertyChanged(nameof(IsTransition));
        OnPropertyChanged(nameof(ModeTitle));
        OnPropertyChanged(nameof(ModeSubtitle));
        OnPropertyChanged(nameof(ModeDescription));
        OnPropertyChanged(nameof(ModeIcon));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(UnprotectedCount));
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(HasUnprotected));
        OnPropertyChanged(nameof(SkippedCountText));
        OnPropertyChanged(nameof(ProblemText));
        OnSavePasswordsChanged();
    }

    #endregion

    #region Saved Steam passwords

    /// <summary>
    ///     "Save Steam passwords for auto-login" (<see cref="Settings.SaveSteamPasswords" />; changeable only in an
    ///     encrypted storage). Turning it on only allows saving again. Turning it off deletes the saved passwords: with
    ///     passwords (or a store file) present it opens the confirmation page and stays on until the user confirms.
    /// </summary>
    public bool SavePasswords
    {
        get => Settings.Instance.SaveSteamPasswords;
        set
        {
            if (value == Settings.Instance.SaveSteamPasswords) return;
            if (value)
            {
                Settings.Instance.SaveSteamPasswords = true;
                Shell.Logger.Info("Saving Steam passwords turned on");
                OnSavePasswordsChanged();
                return;
            }

            RequestTurnOffSaving();
        }
    }

    /// <summary>
    ///     The switch of the tab: shows "off" while saving is impossible (not encrypted), whatever the setting says.
    /// </summary>
    public bool SavePasswordsSwitch
    {
        get => IsEncrypted && SavePasswords;
        set
        {
            if (!IsEncrypted) return;
            SavePasswords = value;
        }
    }

    /// <summary>Muted count after the toggle title: "(saved: 3)" (only while saving is possible and on).</summary>
    public string? SavedPasswordsText => IsEncrypted && SavePasswords
        ? string.Format(Loc("(saved: {0})", "SavePasswords", "SavedCount"), SteamPasswords.Count)
        : null;

    /// <summary>One line under the toggle row: why it is disabled (not encrypted); nothing otherwise.</summary>
    public string? SavePasswordsNote => IsEncrypted
        ? null
        : Loc("Requires encryption: passwords are saved only in an encrypted storage", "SavePasswords",
            "NeedsEncryptionShort");

    /// <summary>Full explanation of the toggle (tooltip).</summary>
    public string SavePasswordsHelper => !IsEncrypted
        ? Loc("Available when encryption is on: passwords are saved only in an encrypted storage.", "SavePasswords",
            "NeedsEncryption")
        : SavePasswords
            ? string.Format(Loc(
                "Passwords are kept in a separate encrypted file and used for automatic re-login. Saved: {0}.",
                "SavePasswords", "OnHelper"), SteamPasswords.Count)
            : Loc("Passwords are not saved and not used for automatic re-login.", "SavePasswords", "OffHelper");

    public string ForgetPasswordsText => string.Format(Loc(
        "Saving Steam passwords will be turned off and the saved passwords ({0}) will be deleted. Automatic re-login will ask for the password again.",
        "SavePasswords", "ForgetText"), SteamPasswords.Count);

    private void RequestTurnOffSaving()
    {
        if (SteamPasswords.Count > 0 || File.Exists(_storage.Layout.PasswordsFile))
        {
            OpenForm(EncryptionPage.ForgetPasswords);
            OnPropertyChanged(nameof(ForgetPasswordsText));
            // The switch was flipped by the user: show "on" again until the deletion is confirmed (a change notification
            // raised inside the setter is ignored by the binding)
            Dispatcher.UIThread.Post(OnSavePasswordsChanged);
            return;
        }

        // Nothing saved: turned off at once (copies of a damaged store are removed too)
        _ = TurnOffSavingAsync(false);
    }

    [RelayCommand]
    private Task ForgetPasswords()
    {
        return TurnOffSavingAsync(true);
    }

    private async Task TurnOffSavingAsync(bool confirmed)
    {
        try
        {
            var deleted = await Task.Run(_storage.ClearSteamPasswords);
            Settings.Instance.SaveSteamPasswords = false;
            Shell.Logger.Info("Saving Steam passwords turned off ({count} deleted)", deleted);
            ClearForm();
            Page = EncryptionPage.Overview;
            Notify(confirmed
                ? string.Format(Loc("Saving Steam passwords is off: {0} saved password(s) deleted.", "SavePasswords",
                    "Deleted"), deleted)
                : Loc("Saving Steam passwords is off.", "SavePasswords", "TurnedOff"));
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Saved Steam passwords can't be deleted");
            Page = EncryptionPage.Overview;
            Notify(string.Format(Loc(
                "The saved passwords could not be deleted: {0}. Saving stays on; try again after closing programs that use the data folder.",
                "SavePasswords", "DeleteFailed"), ex.Message));
        }

        OnSavePasswordsChanged();
    }

    private void OnSavePasswordsChanged()
    {
        OnPropertyChanged(nameof(SavePasswords));
        OnPropertyChanged(nameof(SavePasswordsSwitch));
        OnPropertyChanged(nameof(SavePasswordsHelper));
        OnPropertyChanged(nameof(SavedPasswordsText));
        OnPropertyChanged(nameof(SavePasswordsNote));
    }

    #endregion

    #region Password strength

    public PasswordStrength Strength => EstimateStrength(NewPassword);
    public bool IsWeak => Strength == PasswordStrength.Weak;
    public bool IsFair => Strength == PasswordStrength.Fair;
    public bool IsStrong => Strength == PasswordStrength.Strong;

    public string? StrengthText => Strength switch
    {
        PasswordStrength.Weak => Loc("Weak", "Strength", "Weak"),
        PasswordStrength.Fair => Loc("Fair", "Strength", "Fair"),
        PasswordStrength.Strong => Loc("Strong", "Strength", "Strong"),
        _ => null
    };

    /// <summary>
    ///     Rough estimate for the hint under the field: length first, then the variety of characters.
    /// </summary>
    public static PasswordStrength EstimateStrength(string? password)
    {
        if (string.IsNullOrEmpty(password)) return PasswordStrength.None;
        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;
        var length = password.Length;
        if (length < MIN_PASSWORD_LENGTH || password.Distinct().Count() < 4) return PasswordStrength.Weak;
        if (length >= 16 || (length >= 12 && classes >= 3)) return PasswordStrength.Strong;
        return length >= 10 || classes >= 3 ? PasswordStrength.Fair : PasswordStrength.Weak;
    }

    #endregion

    #region Navigation

    [RelayCommand]
    private void ShowEnable()
    {
        OpenForm(EncryptionPage.Enable);
        ConfirmEncryptedFiles = false;
        try
        {
            EncryptedFilesCount = _storage.CountEncryptedFiles();
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "Encrypted files can't be counted");
            EncryptedFilesCount = 0;
        }
    }

    [RelayCommand]
    private void ShowChange()
    {
        OpenForm(EncryptionPage.Change);
    }

    [RelayCommand]
    private void ShowDisable()
    {
        OpenForm(EncryptionPage.Disable);
    }

    [RelayCommand]
    private void ShowSkipped()
    {
        Refresh();
        Page = EncryptionPage.Skipped;
    }

    [RelayCommand]
    private void Back()
    {
        ClearForm();
        Page = EncryptionPage.Overview;
    }

    private void OpenForm(EncryptionPage page)
    {
        ClearForm();
        Page = page;
    }

    private void ClearForm()
    {
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        CurrentPassword = string.Empty;
        FormError = null;
    }

    #endregion

    #region Operations

    [RelayCommand]
    private async Task Enable()
    {
        if (!ValidateNewPassword()) return;
        if (HasEncryptedFiles && !ConfirmEncryptedFiles)
        {
            FormError = Loc("Confirm that the encrypted files stay unreadable, or restore their storage state first.",
                "Errors", "EncryptedFilesNotConfirmed");
            return;
        }

        var password = NewPassword;
        var accept = ConfirmEncryptedFiles;
        await RunBulkAsync(Loc("Encrypting files…", "Progress", "Encrypting"),
            progress => _storage.EnableEncryptionAsync(password, progress, acceptEncryptedFiles: accept),
            Loc("Encryption is enabled: {0} file(s) encrypted.", "Result", "Enabled"));
    }

    [RelayCommand]
    private async Task Disable()
    {
        string? current = null;
        if (DisableRequiresPassword)
        {
            FormError = null;
            if (string.IsNullOrEmpty(CurrentPassword))
            {
                FormError = Loc("Enter the current master password.", "Errors", "CurrentEmpty");
                return;
            }

            current = CurrentPassword;
        }

        await RunBulkAsync(Loc("Decrypting files…", "Progress", "Decrypting"),
            progress => _storage.DisableEncryptionAsync(progress, currentPassword: current),
            Loc("Encryption is disabled: {0} file(s) decrypted, saved Steam passwords deleted.", "Result",
                "Disabled"));
    }

    [RelayCommand]
    private async Task Resume()
    {
        var title = Mode == StorageMode.Decrypting
            ? Loc("Decrypting files…", "Progress", "Decrypting")
            : Loc("Encrypting files…", "Progress", "Encrypting");
        var done = Mode == StorageMode.Decrypting
            ? Loc("Encryption is disabled: {0} file(s) decrypted, saved Steam passwords deleted.", "Result", "Disabled")
            : Loc("Encryption is enabled: {0} file(s) encrypted.", "Result", "Enabled");
        await RunBulkAsync(title, progress => _storage.ResumeTransitionAsync(progress), done);
    }

    [RelayCommand]
    private async Task Change()
    {
        if (string.IsNullOrEmpty(CurrentPassword))
        {
            FormError = Loc("Enter the current master password.", "Errors", "CurrentEmpty");
            return;
        }

        if (!ValidateNewPassword()) return;
        var current = CurrentPassword;
        var password = NewPassword;
        BeginBusy(Loc("Changing the master password…", "Progress", "Changing"));
        try
        {
            await Task.Run(() => _storage.ChangeMasterPasswordAsync(current, password));
            ClearForm();
            Page = EncryptionPage.Overview;
            Notify(Loc("The master password is changed. Use the new password on the next start.", "Result",
                "Changed"));
        }
        catch (StorageAccessException ex) when (ex.Error == StorageAccessError.WrongPassword)
        {
            Shell.Logger.Warn("Change master password: the current password is wrong");
            FormError = Loc("The current master password is wrong.", "Errors", "WrongCurrent");
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Change master password failed");
            FormError = string.Format(Loc("The master password was not changed: {0}", "Errors", "ChangeFailed"),
                ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private bool ValidateNewPassword()
    {
        FormError = null;
        if (NewPassword.Length < MIN_PASSWORD_LENGTH)
        {
            FormError = string.Format(Loc("The master password must have at least {0} characters.", "Errors",
                "TooShort"), MIN_PASSWORD_LENGTH);
            return false;
        }

        if (NewPassword != ConfirmPassword)
        {
            FormError = Loc("The passwords do not match.", "Errors", "Mismatch");
            return false;
        }

        return true;
    }

    private async Task RunBulkAsync(string title,
        Func<IProgress<StorageProgress>, Task<StorageOperationResult>> operation, string completedFormat)
    {
        BeginBusy(title);
        IProgress<StorageProgress> progress = new Progress<StorageProgress>(OnProgress);
        DebugWrapProgress(ref progress);
        var keepForm = false;
        try
        {
            var result = await operation(progress);
            if (result.Completed)
            {
                var text = string.Format(completedFormat, result.Converted);
                if (result.Skipped.Count > 0)
                {
                    text += " " + string.Format(Loc("{0} file(s) were skipped: see the list.", "Result", "Skipped"),
                        result.Skipped.Count);
                }

                Notify(text, result.Skipped.Count > 0);
            }
            else if (result.ModeAfter == StorageMode.Decrypting)
            {
                // The key (and the saved Steam passwords) are kept while a file still needs it
                Notify(string.Format(Loc(
                    "Encryption is still on: {0} file(s) are still encrypted with your key or could not be checked (see the list). The key and the saved Steam passwords are kept until every file is decrypted. Close the program that uses these files or fix or remove them, then resume.",
                    "Result", "NotFinishedDecrypting"), result.Failed.Count), true);
            }
            else
            {
                Notify(string.Format(Loc(
                    "The operation is not finished: {0} file(s) could not be converted and stay unchanged (see the list). Resume to try again; it is also resumed on the next start.",
                    "Result", "NotFinished"), result.Failed.Count), true);
            }
        }
        catch (StorageAccessException ex) when (ex.Error == StorageAccessError.WrongPassword)
        {
            Shell.Logger.Warn("Storage operation: the current master password is wrong");
            keepForm = true;
            FormError = Loc("The current master password is wrong.", "Errors", "WrongCurrent");
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Storage operation failed: {title}", title);
            Notify(string.Format(Loc(
                "The operation failed and is not finished: {0}. No file was lost; if the mode shows an unfinished change, resume it.",
                "Result", "Failed"), ex.Message));
        }
        finally
        {
            if (!keepForm)
            {
                ClearForm();
                Page = EncryptionPage.Overview;
            }

            EndBusy();
        }
    }

    private void OnProgress(StorageProgress p)
    {
        if (p.Total <= 0) return;
        IsProgressIndeterminate = false;
        ProgressValue = p.Processed / (double) p.Total;
        ProgressCount = string.Format(Loc("{0} of {1}", "Progress", "Count"), p.Processed, p.Total);
        ProgressFile = p.CurrentFile;
    }

    private void BeginBusy(string title)
    {
        StorageOperationLock.Enter();
        ProgressTitle = title;
        ProgressValue = 0;
        ProgressCount = null;
        ProgressFile = null;
        IsProgressIndeterminate = true;
        IsBusy = true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        StorageOperationLock.Exit();
        Refresh();
    }

    /// <summary>
    ///     Result of an operation: a snackbar, never a message kept in the tab (it would go stale after the next change of
    ///     the state). <paramref name="showList" /> adds a button that opens the skipped files list.
    /// </summary>
    private void Notify(string text, bool showList = false)
    {
        // Long enough to read a result even when it is short
        var duration = TimeSpan.FromMilliseconds(Math.Max(RESULT_MIN_DURATION_MS, text.Length * 60));
        if (showList)
            SnackbarController.SendSnackbarWithButton(text, Loc("Show", "Problems", "Show"), ShowSkipped, duration);
        else
            SnackbarController.SendSnackbar(text, duration);
    }

    partial void DebugWrapProgress(ref IProgress<StorageProgress> progress);
    partial void DebugOverrideMode(ref StorageMode mode);

    #endregion

    private static string Loc(string def, params string[] path)
    {
        return LocManager.GetOrDefault(def, ["SettingsDialog", "Encryption", ..path]);
    }
}
