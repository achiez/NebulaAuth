using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Credentials;
using NebulaAuth.NebulaLink.Protocol;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     The device credential of the link: kept in a secure store (encrypted storage or the OS store), loaded at start,
///     moved when the storage mode changes, rotated on the server's request and deleted with the link. In memory it is
///     registered as a runtime secret (never logged).
/// </summary>
internal sealed class DeviceCredentialManager(DeviceCredentialStoreSelector selector, LinkStore links, Action? purgeEncrypted)
{
    private string? _current;

    /// <summary>The credential of the current link in memory; null when not loaded.</summary>
    public string? Current => _current;

    public Task<IDeviceCredentialStore?> ProbeAsync()
    {
        return selector.SelectAsync();
    }

    /// <summary>Loads the credential of the link (local store only); null when it can't be read.</summary>
    public async Task<string?> LoadAsync(Guid linkId, string? kind)
    {
        var store = selector.Get(kind);
        if (store == null)
        {
            NebulaLinkLog.Warn($"The credential store '{kind}' is not available now");
            return null;
        }

        string? credential;
        try
        {
            credential = await store.LoadAsync(linkId);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Error("The device credential can't be read", ex);
            return null;
        }

        if (credential == null) return null;
        // Encryption was enabled while the credential was in the OS store: move it into the encrypted storage
        if (kind == IDeviceCredentialStore.KIND_OS && selector.Get(IDeviceCredentialStore.KIND_ENCRYPTED) != null)
            await MigrateAsync(linkId, credential, kind);
        SetCurrent(credential);
        return credential;
    }

    /// <summary>Stores the credential of a new link; the store used, or null when no secure store could keep it.</summary>
    public async Task<IDeviceCredentialStore?> StoreNewAsync(Guid linkId, string credential)
    {
        RuntimeSecrets.Register(credential);
        var store = await selector.SelectAsync();
        if (store == null)
        {
            NebulaLinkLog.Error("No secure store for the device credential: the link is not kept");
            return null;
        }

        try
        {
            await store.SaveAsync(linkId, credential);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Error("The device credential can't be stored: the link is not kept", ex);
            return null;
        }

        SetCurrent(credential);
        return store;
    }

    /// <summary>Deletes the credential of a link from every store it may be in.</summary>
    public async Task DeleteAsync(Guid linkId, string? storeKind)
    {
        foreach (var store in new[] {selector.Get(storeKind), selector.Get(IDeviceCredentialStore.KIND_OS)}.Distinct())
        {
            if (store == null) continue;
            try
            {
                await store.DeleteAsync(linkId);
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn($"The device credential can't be deleted from {store.DisplayName}", ex);
            }
        }

        if (storeKind == IDeviceCredentialStore.KIND_ENCRYPTED) purgeEncrypted?.Invoke();
    }

    /// <summary>Forgets the credential in memory.</summary>
    public void Forget()
    {
        SetCurrent(null);
    }

    /// <summary>
    ///     The app's storage mode changed: the credential moves to the store of the new mode; false when no store can keep
    ///     it (it stays in memory for this session only).
    /// </summary>
    public async Task<bool> OnStorageModeChangedAsync(bool encryptionOff)
    {
        var data = links.Snapshot();
        if (data?.LinkId is not { } linkId || _current == null) return true;
        var migrated = await MigrateAsync(linkId, _current, data.CredentialStore);
        if (encryptionOff) purgeEncrypted?.Invoke();
        return migrated;
    }

    /// <summary>The server asked for a new credential: it is written to the store before it is used.</summary>
    public async Task RotateAsync(NebulaConnection connection, CancellationToken ct)
    {
        var data = links.Snapshot();
        if (data?.LinkId is not { } linkId) return;
        try
        {
            var rotated = await connection.InvokeAsync<RotateCredentialResponse>("RotateCredential", null, ct);
            var store = selector.Get(data.CredentialStore) ?? throw new CredentialStoreException("No store");
            // When writing fails the previous credential stays valid until the server sees the new one; it asks again
            // on the next Hello
            await store.SaveAsync(linkId, rotated.Credential);
            SetCurrent(rotated.Credential);
            NebulaLinkLog.Info($"Device credential rotated (generation {rotated.CredentialGeneration})");
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Credential rotation failed; retried on the next Hello", ex);
        }
    }

    private async Task<bool> MigrateAsync(Guid linkId, string credential, string? currentKind)
    {
        try
        {
            var store = await selector.MigrateAsync(linkId, credential, currentKind);
            if (store == null)
            {
                links.Update(d => d.CredentialStore = null);
                return false;
            }

            if (store.Kind != currentKind) links.Update(d => d.CredentialStore = store.Kind);
            return true;
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Error("The device credential can't be moved to the new store", ex);
            return false;
        }
    }

    private void SetCurrent(string? credential)
    {
        var old = Interlocked.Exchange(ref _current, credential);
        if (credential != null) RuntimeSecrets.Register(credential);
        if (old != null && old != credential) RuntimeSecrets.Unregister(old);
    }
}
