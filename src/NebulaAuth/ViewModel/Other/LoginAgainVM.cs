using CommunityToolkit.Mvvm.ComponentModel;
using NebulaAuth.Model;

namespace NebulaAuth.ViewModel.Other;

public partial class LoginAgainVM : ObservableObject
{
    public bool IsFormValid => !string.IsNullOrWhiteSpace(Password);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFormValid))]
    private string _password = null!;

    [ObservableProperty] private bool _savePassword;

    /// <summary>
    ///     Passwords can be saved only in an encrypted storage; otherwise the password is used for this login only.
    /// </summary>
    [ObservableProperty] private bool _canSavePassword = SteamPasswords.CanSave;

    /// <summary>
    ///     The storage is encrypted, but saving Steam passwords is turned off in the settings (hint text).
    /// </summary>
    [ObservableProperty] private bool _saveTurnedOff = SteamPasswords.IsTurnedOff;

    [ObservableProperty] private bool _showNoProxyWarning;

    [ObservableProperty] private string _userName = null!;
}