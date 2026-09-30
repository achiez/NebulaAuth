using System;
using System.IO;
using NebulaAuth.Model.Mafiles.Protection;

namespace NebulaAuth.Model;

/// <summary>
///     Saved Steam passwords for automatic re-login (by Steam login, <see cref="Entities.Mafile.AccountName" />). They
///     exist only while the storage is encrypted and unlocked (<see cref="SteamPasswordStore" />) and the user allows
///     saving them (<see cref="SavingEnabled" />, setting <see cref="Settings.SaveSteamPasswords" />). Otherwise nothing
///     can be saved and nothing is returned. A password typed for a single login is used only for that login.
/// </summary>
public static class SteamPasswords
{
    private static SteamPasswordStore? Store => MafileStorageService.Current.Passwords;

    /// <summary>
    ///     The user allows saving Steam passwords (<see cref="Settings.SaveSteamPasswords" /> of the app settings, kept in
    ///     sync by <see cref="Settings" />). When false the saved passwords are neither read nor written.
    /// </summary>
    public static bool SavingEnabled { get; set; } = true;

    /// <summary>
    ///     Passwords can be saved (encrypted storage, unlocked, saving allowed).
    /// </summary>
    public static bool CanSave => SavingEnabled && Store != null;

    /// <summary>
    ///     The storage could hold passwords, but the user turned saving off (the hint points to the setting, not to
    ///     encryption).
    /// </summary>
    public static bool IsTurnedOff => !SavingEnabled && Store != null;

    /// <summary>
    ///     Number of saved passwords (0 when not available).
    /// </summary>
    public static int Count => Store?.Count ?? 0;

    public static string? Get(string? login)
    {
        return SavingEnabled ? Store?.Get(login) : null;
    }

    public static bool Has(string? login)
    {
        return SavingEnabled && Store?.Contains(login) == true;
    }

    /// <summary>
    ///     Saves the password; false when saving is not available (plain storage, turned off) or failed.
    /// </summary>
    public static bool TrySet(string? login, string? password)
    {
        if (!SavingEnabled) return false;
        var store = Store;
        if (store == null || string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password)) return false;
        try
        {
            store.Set(login, password);
            return true;
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Can't save the Steam password of {login}", login);
            return false;
        }
    }

    public static void Remove(string? login)
    {
        try
        {
            Store?.Remove(login);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Can't remove the Steam password of {login}", login);
        }
    }

    /// <summary>
    ///     Deletes every saved password: the store is emptied and its file removed (saving turned off by the user).
    ///     Works whether saving is allowed or not; a later save creates a new file.
    /// </summary>
    /// <returns>Number of deleted passwords.</returns>
    /// <exception cref="IOException">The store file can't be changed or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">No access to the store file.</exception>
    public static int ClearAll()
    {
        return MafileStorageService.Current.ClearSteamPasswords();
    }
}
