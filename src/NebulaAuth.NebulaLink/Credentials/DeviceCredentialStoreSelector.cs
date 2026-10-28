using System;
using System.Threading.Tasks;

namespace NebulaAuth.NebulaLink.Credentials;

/// <summary>
///     Picks where the device credential is kept: the encrypted storage when encryption is on and unlocked, otherwise the
///     store of the operating system. Each candidate is probed; null = no secure store (pairing is blocked).
/// </summary>
public class DeviceCredentialStoreSelector
{
    private readonly Func<IDeviceCredentialStore?> _encrypted;
    private readonly Func<IDeviceCredentialStore?> _os;

    public DeviceCredentialStoreSelector(Func<IDeviceCredentialStore?> encrypted, Func<IDeviceCredentialStore?> os)
    {
        _encrypted = encrypted;
        _os = os;
    }

    /// <summary>
    ///     The app's encrypted store (null while encryption is off or locked) + the OS store of this platform.
    /// </summary>
    public static DeviceCredentialStoreSelector CreateDefault(Func<IDeviceCredentialStore?> encrypted)
    {
        return new DeviceCredentialStoreSelector(encrypted, CreateOsStore);
    }

    public static IDeviceCredentialStore? CreateOsStore()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainCredentialStore();
        if (OperatingSystem.IsLinux()) return new LibSecretCredentialStore();
        return null;
    }

    /// <summary>The store to use now (probed), or null when none works.</summary>
    public virtual async Task<IDeviceCredentialStore?> SelectAsync()
    {
        var encrypted = _encrypted();
        if (encrypted != null && await SafeProbe(encrypted)) return encrypted;
        var os = _os();
        if (os != null && await SafeProbe(os)) return os;
        return null;
    }

    /// <summary>The store of a given kind (the one nebula.json names), not probed; null when not available now.</summary>
    public virtual IDeviceCredentialStore? Get(string? kind)
    {
        return kind switch
        {
            IDeviceCredentialStore.KIND_ENCRYPTED => _encrypted(),
            IDeviceCredentialStore.KIND_OS => _os(),
            _ => null
        };
    }

    private static async Task<bool> SafeProbe(IDeviceCredentialStore store)
    {
        try
        {
            return await store.ProbeAsync();
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn($"Credential store probe failed: {store.DisplayName}", ex);
            return false;
        }
    }

    /// <summary>
    ///     Moves the credential to the store that matches the current storage mode (encryption on: encrypted storage,
    ///     then the OS item is deleted; off: OS store). Returns the new store, or null when there is no secure store for
    ///     the new mode (the link is unusable until encryption is enabled again or the device is unlinked).
    /// </summary>
    public async Task<IDeviceCredentialStore?> MigrateAsync(Guid linkId, string credential, string? currentKind)
    {
        var target = await SelectAsync();
        if (target == null)
        {
            NebulaLinkLog.Warn("No secure store for the device credential after the storage mode change");
            return null;
        }

        if (target.Kind == currentKind)
        {
            // Same kind: re-save (the encrypted file must be written with the new key after a re-enable)
            await target.SaveAsync(linkId, credential);
            return target;
        }

        await target.SaveAsync(linkId, credential);
        var old = Get(currentKind);
        if (old != null)
        {
            try
            {
                await old.DeleteAsync(linkId);
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("The previous copy of the device credential can't be deleted", ex);
            }
        }

        NebulaLinkLog.Info($"Device credential moved to {target.DisplayName}");
        return target;
    }
}
