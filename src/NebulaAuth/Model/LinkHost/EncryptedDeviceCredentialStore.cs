using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Credentials;
using Newtonsoft.Json.Linq;

namespace NebulaAuth.Model.LinkHost;

/// <summary>
///     The device credential in nebula-device.encrypted, encrypted with the storage key (own HKDF subkey, content kind
///     <see cref="EncryptedContentKind.NebulaDevice" />). Usable only while the storage is encrypted and unlocked.
/// </summary>
public sealed class EncryptedDeviceCredentialStore(MafileStorageService storage) : IDeviceCredentialStore
{
    private const int PAYLOAD_VERSION = 1;

    public string Kind => IDeviceCredentialStore.KIND_ENCRYPTED;
    public string DisplayName => "NebulaAuth encrypted storage";

    public Task<bool> ProbeAsync()
    {
        if (!storage.CanProtectNebulaDevice) return Task.FromResult(false);
        try
        {
            var probe = RandomNumberGenerator.GetBytes(32);
            var back = storage.DecryptNebulaDevice(storage.EncryptNebulaDevice(probe));
            var ok = back.AsSpan().SequenceEqual(probe) && Directory.Exists(Path.GetDirectoryName(storage.NebulaDeviceFile));
            return Task.FromResult(ok);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Encrypted credential store probe failed", ex);
            return Task.FromResult(false);
        }
    }

    public Task SaveAsync(Guid linkId, string credential)
    {
        var j = new JObject {["version"] = PAYLOAD_VERSION, ["linkId"] = linkId.ToString("N"), ["credential"] = credential};
        var plain = Encoding.UTF8.GetBytes(j.ToString(Newtonsoft.Json.Formatting.None));
        try
        {
            var data = storage.EncryptNebulaDevice(plain);
            AtomicFile.Write(storage.NebulaDeviceFile, data, true, written =>
            {
                try
                {
                    var check = storage.DecryptNebulaDevice(written);
                    var same = check.AsSpan().SequenceEqual(plain);
                    CryptographicOperations.ZeroMemory(check);
                    return same ? null : "decrypted content differs";
                }
                catch (EnvelopeException ex)
                {
                    return ex.Error.ToString();
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new CredentialStoreException("The device credential can't be written to the encrypted storage", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(Guid linkId)
    {
        var path = storage.NebulaDeviceFile;
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        byte[]? plain = null;
        try
        {
            plain = storage.DecryptNebulaDevice(File.ReadAllBytes(path));
            var j = JObject.Parse(Encoding.UTF8.GetString(plain));
            if (j.Value<int?>("version") != PAYLOAD_VERSION) return Task.FromResult<string?>(null);
            if (!Guid.TryParse(j.Value<string>("linkId"), out var stored) || stored != linkId)
                return Task.FromResult<string?>(null);
            return Task.FromResult(j.Value<string>("credential"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or EnvelopeException or Newtonsoft.Json.JsonException)
        {
            throw new CredentialStoreException("The device credential can't be read from the encrypted storage", ex);
        }
        finally
        {
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task DeleteAsync(Guid linkId)
    {
        AtomicFile.TryDelete(storage.NebulaDeviceFile);
        return Task.CompletedTask;
    }
}
