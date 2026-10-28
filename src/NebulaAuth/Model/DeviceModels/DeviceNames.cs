using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using SteamLib.Factory.Helpers;
using SteamLib.ProtoCore.Services;

namespace NebulaAuth.Model.DeviceModels;

/// <summary>
///     Device names of the accounts (<see cref="Mafile.DeviceName" />) and the user's model list (built-in models minus the
///     removed ones + custom names; at least one model). A new authenticator (link / move) gets a random model once before
///     its first request; any other account without a name gets one only right before its first mobile login
///     (<see cref="PrepareLoginDeviceAsync" />). Import / restore keep the name of the file (or none). An assigned name is
///     never changed.
/// </summary>
public static class DeviceNames
{
    private static readonly Lock ListLock = new();

    /// <summary>The user's model list (snapshot): custom names first, then the built-in models that were not removed.</summary>
    public static IReadOnlyList<DeviceModelEntry> GetEntries()
    {
        lock (ListLock)
        {
            return GetEntriesLocked();
        }
    }

    /// <summary>Number of built-in models the user removed from the list.</summary>
    public static int GetRemovedBuiltInCount()
    {
        lock (ListLock)
        {
            return DeviceModelCatalog.BuiltIn.Count - GetEntriesLocked().Count(e => e.IsBuiltIn);
        }
    }

    /// <summary>
    ///     Adds a model name (trimmed) to the list and saves the settings. A name equal (up to case) to a removed built-in
    ///     model puts that built-in model back instead of adding a custom name (<paramref name="entry" /> is then the
    ///     built-in entry). Returns the validation result; nothing changes unless it is <see cref="DeviceModelValidation.Ok" />.
    /// </summary>
    public static DeviceModelValidation AddModel(string? input, out DeviceModelEntry? entry)
    {
        lock (ListLock)
        {
            entry = null;
            var current = GetEntriesLocked();
            var result = DeviceModelCatalog.ValidateName(input, current.Select(e => e.Name), out var name);
            if (result != DeviceModelValidation.Ok) return result;

            var settings = Settings.Instance;
            var builtIn = DeviceModelCatalog.FindBuiltIn(name);
            if (builtIn != null)
            {
                settings.RemovedBuiltInDeviceModels.RemoveAll(r =>
                    string.Equals(r?.Trim(), builtIn.Model, StringComparison.OrdinalIgnoreCase));
                entry = new DeviceModelEntry(builtIn.Model, builtIn);
            }
            else
            {
                settings.CustomDeviceModels.Add(name);
                entry = new DeviceModelEntry(name, null);
            }

            Settings.Save();
            return result;
        }
    }

    /// <summary>
    ///     Removes a model (built-in or custom, matched up to case) from the list and saves the settings. Refused (false)
    ///     when it is not in the list or is the last model: at least one model always remains. Accounts keep their names.
    /// </summary>
    public static bool RemoveModel(string? name)
    {
        lock (ListLock)
        {
            var current = GetEntriesLocked();
            var entry = current.FirstOrDefault(e =>
                string.Equals(e.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (entry == null || current.Count <= 1) return false;

            var settings = Settings.Instance;
            // A custom copy of a built-in name (hand-edited settings) goes too, otherwise it would take its place
            settings.CustomDeviceModels.RemoveAll(c =>
                string.Equals(c?.Trim(), entry.Name, StringComparison.OrdinalIgnoreCase));
            if (entry.IsBuiltIn &&
                !settings.RemovedBuiltInDeviceModels.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                settings.RemovedBuiltInDeviceModels.Add(entry.Name);
            Settings.Save();
            return true;
        }
    }

    /// <summary>Puts every removed built-in model back into the list and saves the settings. Returns how many.</summary>
    public static int RestoreBuiltInModels()
    {
        lock (ListLock)
        {
            var before = GetEntriesLocked().Count;
            var removed = Settings.Instance.RemovedBuiltInDeviceModels;
            if (removed.Count == 0) return 0;
            removed.Clear();
            Settings.Save();
            return GetEntriesLocked().Count - before;
        }
    }

    /// <summary>Names a new account can get (the user's list; never empty).</summary>
    public static IReadOnlyList<string> GetPool()
    {
        lock (ListLock)
        {
            var settings = Settings.Instance;
            return DeviceModelCatalog.GetPool(settings.RemovedBuiltInDeviceModels, settings.CustomDeviceModels);
        }
    }

    /// <summary>A random model of the user's list (built-in models that were not removed + custom names).</summary>
    public static string PickRandom()
    {
        return DeviceModelCatalog.PickRandom(GetPool());
    }

    /// <summary>
    ///     The list from the settings. Settings that leave no model at all (edited by hand) are treated as "nothing
    ///     removed", the same list new accounts get (<see cref="DeviceModelCatalog.GetPool" />); the next change saves it.
    /// </summary>
    private static IReadOnlyList<DeviceModelEntry> GetEntriesLocked()
    {
        var settings = Settings.Instance;
        var entries = DeviceModelCatalog.GetEntries(settings.RemovedBuiltInDeviceModels, settings.CustomDeviceModels);
        if (entries.Count > 0) return entries;
        settings.RemovedBuiltInDeviceModels.Clear();
        return DeviceModelCatalog.GetEntries(null, settings.CustomDeviceModels);
    }

    public static bool HasName(Mafile mafile)
    {
        return !string.IsNullOrWhiteSpace(mafile.DeviceName);
    }

    /// <summary>
    ///     Device details of a mobile login / re-login of the account (called right before the login request). An account
    ///     without a device name gets a random model of the list now, and an account of the storage (it has a file name)
    ///     gets it written to its mafile (storage layer: atomic, plain or encrypted) BEFORE the request, so a failed login
    ///     keeps it and a retry uses the same device. When that write fails the exception goes to the caller (no request
    ///     is sent) and the name stays on the account in memory: the retry sends the same name and writes it again.
    ///     A mafile not in the storage yet (import of a mafile without a session: the file is written only after a
    ///     successful login) is not written here; its name is kept in memory for this account, so a retry of that
    ///     import in this session uses the same name, and the mafile saved after the login contains it.
    ///     An existing name is never changed; the other platform values are the default ones.
    /// </summary>
    public static async Task<DeviceDetails> PrepareLoginDeviceAsync(Mafile mafile)
    {
        if (!HasName(mafile)) AssignForLogin(mafile);

        if (mafile.Filename != null && UnsavedNames.TryGetValue(mafile, out _))
        {
            await Storage.UpdateMafileAsync(mafile);
            UnsavedNames.Remove(mafile);
            Shell.Logger.Info("Device name {device} assigned to mafile {file}", mafile.DeviceName, mafile.Filename);
        }

        return DeviceDetailsDefaultBuilder.GetMobileDevice(mafile.DeviceName);
    }

    private static void AssignForLogin(Mafile mafile)
    {
        var account = mafile.AccountName;
        lock (PendingImportNames)
        {
            if (mafile.Filename == null && !string.IsNullOrEmpty(account) &&
                PendingImportNames.TryGetValue(account, out var pending))
            {
                mafile.DeviceName = pending;
            }
            else
            {
                mafile.DeviceName = PickRandom();
                if (mafile.Filename == null && !string.IsNullOrEmpty(account))
                    PendingImportNames[account] = mafile.DeviceName;
            }
        }

        if (mafile.Filename != null) UnsavedNames.AddOrUpdate(mafile, UnsavedMarker);
    }

    // Accounts of the storage whose name (assigned for a login) is not written to the mafile yet
    private static readonly ConditionalWeakTable<Mafile, object> UnsavedNames = new();
    private static readonly object UnsavedMarker = new();

    // Names chosen for logins of mafiles not in the storage yet (import without a session), by account name
    private static readonly Dictionary<string, string> PendingImportNames = new(StringComparer.OrdinalIgnoreCase);
}
