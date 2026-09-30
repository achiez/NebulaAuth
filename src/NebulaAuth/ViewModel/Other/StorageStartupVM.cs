using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NebulaAuth.Core;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.Other;

public enum StorageStartupPage
{
    /// <summary>Master password prompt.</summary>
    Unlock,

    /// <summary>An interrupted enable / disable of encryption is finished before the accounts are loaded.</summary>
    Progress,

    /// <summary>The storage can't be opened (damaged / missing state file, newer format): only quit.</summary>
    Error,

    /// <summary>
    ///     An interrupted enable / disable could not be finished (some files failed): explained before the accounts open.
    /// </summary>
    Unfinished
}

/// <summary>
///     State of the startup storage window (<see cref="View.StorageStartupWindow" />), shown before the main window
///     exists. The flow itself (unlock attempts, reconciliation) is run by <see cref="StartupStorage" />.
/// </summary>
public partial class StorageStartupVM : ObservableObject
{
    private TaskCompletionSource<string?>? _submit;
    private TaskCompletionSource<bool>? _continue;

    // Typing hints of the current unlock attempt (the password itself is never kept outside Password)
    private bool _capsLockSeen;
    private bool _attemptCapsLock;
    private bool _attemptNonLatin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlockPage), nameof(IsProgressPage), nameof(IsErrorPage),
        nameof(IsUnfinishedPage), nameof(HeaderTitle), nameof(HeaderIcon))]
    private StorageStartupPage _page;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private string _password = string.Empty;

    [ObservableProperty] private string? _passwordError;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private bool _isChecking;

    [ObservableProperty] private string? _errorTitle;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _errorDetails;

    [ObservableProperty] private string? _progressTitle;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private string? _progressCount;
    [ObservableProperty] private string? _progressFile;

    [ObservableProperty] private string? _unfinishedTitle;
    [ObservableProperty] private string? _unfinishedText;
    [ObservableProperty] private SkippedFilesVM _unfinishedFiles = new([]);

    public bool IsUnlockPage => Page == StorageStartupPage.Unlock;
    public bool IsProgressPage => Page == StorageStartupPage.Progress;
    public bool IsErrorPage => Page == StorageStartupPage.Error;
    public bool IsUnfinishedPage => Page == StorageStartupPage.Unfinished;

    public string HeaderTitle => Page switch
    {
        StorageStartupPage.Progress => ProgressTitle ?? string.Empty,
        StorageStartupPage.Error => Loc("Can't open the accounts", "ErrorHeader"),
        StorageStartupPage.Unfinished => UnfinishedTitle ?? string.Empty,
        _ => Loc("Unlock NebulaAuth", "Title")
    };

    public MaterialIconKind HeaderIcon => Page switch
    {
        StorageStartupPage.Progress => MaterialIconKind.ShieldSyncOutline,
        StorageStartupPage.Error => MaterialIconKind.AlertCircleOutline,
        StorageStartupPage.Unfinished => MaterialIconKind.ShieldAlertOutline,
        _ => MaterialIconKind.ShieldLockOutline
    };

    /// <summary>
    ///     Raised when the user wants to leave (Quit button). The window closes itself.
    /// </summary>
    public event System.EventHandler? QuitRequested;

    /// <summary>
    ///     Completes with the entered password, or null when the user quits.
    /// </summary>
    public Task<string?> WaitForPasswordAsync()
    {
        _submit = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsChecking = false;
        return _submit.Task;
    }

    /// <summary>
    ///     Caps Lock state of the password field (<see cref="PasswordHints.IsCapsLockOn" />): an "on" seen while typing is
    ///     remembered until the password is submitted.
    /// </summary>
    public void SetCapsLockOn(bool isOn)
    {
        if (isOn) _capsLockSeen = true;
    }

    // Typing again starts a new attempt: the error of the previous one goes away
    partial void OnPasswordChanged(string value)
    {
        if (!IsChecking) PasswordError = null;
    }

    public void ShowWrongPassword()
    {
        var text = Loc("Wrong master password. Try again.", "WrongPassword");
        if (_attemptCapsLock)
            text += " " + LocManager.GetOrDefault("Caps Lock is on.", "PasswordHints", "CapsLockShort");
        if (_attemptNonLatin)
            text += " " + LocManager.GetOrDefault(
                "The password contains non-Latin characters: check the keyboard layout.", "PasswordHints",
                "NonLatinShort");
        PasswordError = text;
    }

    public void ShowError(StorageAccessException ex)
    {
        (ErrorTitle, ErrorText) = ex.Error switch
        {
            StorageAccessError.UnsupportedVersion => (
                Loc("Newer storage format", "UnsupportedVersionTitle"),
                StartupStorage.DescribeErrorShort(ex)),
            StorageAccessError.StateMissing => (
                Loc("The storage state file is missing", "StateMissingTitle"),
                StartupStorage.DescribeErrorShort(ex)),
            _ => (Loc("The storage state file is damaged", "CorruptedStateTitle"),
                StartupStorage.DescribeErrorShort(ex))
        };
        ErrorDetails = ex.Message;
        Page = StorageStartupPage.Error;
    }

    public void ShowProgress(StorageMode mode)
    {
        ProgressTitle = mode == StorageMode.Decrypting
            ? Loc("Finishing decryption", "ProgressDecrypting")
            : Loc("Finishing encryption", "ProgressEncrypting");
        IsProgressIndeterminate = true;
        ProgressCount = null;
        ProgressFile = null;
        Page = StorageStartupPage.Progress;
        OnPropertyChanged(nameof(HeaderTitle));
    }

    /// <summary>
    ///     Explains an interrupted mode change that could not be finished (<paramref name="failed" /> files). Completes
    ///     with true when the user opens the accounts, false when they quit.
    /// </summary>
    public Task<bool> ShowUnfinishedAsync(StorageMode mode, IReadOnlyCollection<SkippedFile> failed)
    {
        _continue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (mode == StorageMode.Decrypting)
        {
            UnfinishedTitle = Loc("Decryption is not finished", "UnfinishedDecryptingTitle");
            UnfinishedText = string.Format(Loc(
                "Disabling encryption could not be finished: {0} file(s) are still encrypted with your key or could not be checked (for example, another program has them open). Encryption stays on and the master password is still needed until they are decrypted. Close the program that uses them or fix the listed files, then use Resume in Settings → Encryption. The accounts open now.",
                "UnfinishedDecrypting"), failed.Count);
        }
        else
        {
            UnfinishedTitle = Loc("Encryption is not finished", "UnfinishedEncryptingTitle");
            UnfinishedText = string.Format(Loc(
                "Enabling encryption could not be finished: {0} file(s) could not be encrypted and stay unchanged. The accounts open now; the operation is retried on the next start or with Resume in Settings → Encryption.",
                "UnfinishedEncrypting"), failed.Count);
        }

        UnfinishedFiles = new SkippedFilesVM(failed);
        Page = StorageStartupPage.Unfinished;
        OnPropertyChanged(nameof(HeaderTitle));
        return _continue.Task;
    }

    [RelayCommand]
    private void Continue()
    {
        _continue?.TrySetResult(true);
    }

    public void ReportProgress(StorageProgress progress)
    {
        if (progress.Total <= 0)
        {
            IsProgressIndeterminate = true;
            return;
        }

        IsProgressIndeterminate = false;
        ProgressValue = progress.Processed / (double) progress.Total;
        ProgressCount = string.Format(Loc("{0} of {1}", "ProgressCount"), progress.Processed, progress.Total);
        ProgressFile = progress.CurrentFile;
    }

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private void Unlock()
    {
        if (!CanUnlock()) return;
        PasswordError = null;
        _attemptCapsLock = _capsLockSeen;
        _attemptNonLatin = PasswordInputCheck.HasNonLatinLetters(Password);
        _capsLockSeen = false;
        IsChecking = true;
        _submit?.TrySetResult(Password);
    }

    private bool CanUnlock()
    {
        return !IsChecking && !string.IsNullOrEmpty(Password);
    }

    [RelayCommand]
    private void Quit()
    {
        _submit?.TrySetResult(null);
        _continue?.TrySetResult(false);
        QuitRequested?.Invoke(this, System.EventArgs.Empty);
    }

    /// <summary>
    ///     The window was closed by the system / title bar: same as Quit.
    /// </summary>
    public void OnWindowClosed()
    {
        _submit?.TrySetResult(null);
        _continue?.TrySetResult(false);
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        PlatformUtils.OpenFolder(DataRoot.Path);
    }

    private static string Loc(string def, string key)
    {
        return LocManager.GetOrDefault(def, "StorageUnlock", key);
    }
}
