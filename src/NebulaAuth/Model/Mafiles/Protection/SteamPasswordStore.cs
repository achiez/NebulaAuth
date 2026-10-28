using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NebulaAuth.Model.Mafiles.Protection;

/// <summary>
///     Steam passwords for automatic re-login, keyed by the Steam login (<see cref="Entities.Mafile.AccountName" />,
///     case-insensitive), not by SteamID. Exists only in an encrypted, unlocked storage: the file is always encrypted
///     (own subkey, content kind <see cref="EncryptedContentKind.SteamPasswords" />), is never part of mafiles or their
///     backups and is deleted when encryption is disabled. Every change is published with <see cref="AtomicFile" />.
/// </summary>
public sealed class SteamPasswordStore : IDisposable
{
    public const string FILE_NAME = "steam-passwords.encrypted";
    private const int PAYLOAD_VERSION = 1;

    private readonly byte[] _key;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _storageId;
    private bool _disposed;

    public string FilePath { get; }

    public int Count
    {
        get
        {
            lock (_lock) return _passwords.Count;
        }
    }

    private SteamPasswordStore(string path, byte[] key, byte[] storageId)
    {
        FilePath = path;
        _key = key;
        _storageId = storageId;
    }

    /// <summary>
    ///     Opens the store (an empty one when the file does not exist yet).
    /// </summary>
    /// <param name="key">Password store subkey; owned by the store from now on (zeroed on dispose).</param>
    /// <exception cref="EnvelopeException">The file is not a valid password store of this storage.</exception>
    /// <exception cref="FormatException">The decrypted content is damaged.</exception>
    internal static SteamPasswordStore Open(string path, byte[] key, byte[] storageId)
    {
        var store = new SteamPasswordStore(path, key, storageId);
        if (!File.Exists(path)) return store;
        var bytes = File.ReadAllBytes(path);
        var plain = StorageCrypto.Decrypt(bytes, key, storageId, EncryptedContentKind.SteamPasswords);
        try
        {
            var j = JObject.Parse(Encoding.UTF8.GetString(plain));
            if (j.Value<int?>("version") is not PAYLOAD_VERSION || j["passwords"] is not JObject passwords)
                throw new FormatException("Unsupported password store content");
            foreach (var p in passwords.Properties())
            {
                if (p.Value.Type == JTokenType.String) store._passwords[p.Name] = p.Value.Value<string>()!;
            }
        }
        catch (JsonException ex)
        {
            throw new FormatException("The password store content is damaged", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return store;
    }

    public string? Get(string? login)
    {
        if (string.IsNullOrWhiteSpace(login)) return null;
        lock (_lock)
        {
            ThrowIfDisposed();
            return _passwords.GetValueOrDefault(login);
        }
    }

    public bool Contains(string? login)
    {
        if (string.IsNullOrWhiteSpace(login)) return false;
        lock (_lock)
        {
            return !_disposed && _passwords.ContainsKey(login);
        }
    }

    public void Set(string login, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        ArgumentException.ThrowIfNullOrEmpty(password);
        lock (_lock)
        {
            ThrowIfDisposed();
            var had = _passwords.TryGetValue(login, out var old);
            _passwords[login] = password;
            try
            {
                Save();
            }
            catch
            {
                if (had) _passwords[login] = old!;
                else _passwords.Remove(login);
                throw;
            }
        }
    }

    public bool Remove(string? login)
    {
        if (string.IsNullOrWhiteSpace(login)) return false;
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_passwords.Remove(login, out var old)) return false;
            try
            {
                Save();
            }
            catch
            {
                _passwords[login] = old;
                throw;
            }

            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_passwords.Count == 0) return;
            var copy = new Dictionary<string, string>(_passwords, StringComparer.OrdinalIgnoreCase);
            _passwords.Clear();
            try
            {
                Save();
            }
            catch
            {
                foreach (var (k, v) in copy) _passwords[k] = v;
                throw;
            }
        }
    }

    private void Save()
    {
        var passwords = new JObject();
        foreach (var (login, password) in _passwords) passwords[login] = password;
        var j = new JObject {["version"] = PAYLOAD_VERSION, ["passwords"] = passwords};
        var plain = Encoding.UTF8.GetBytes(j.ToString(Formatting.None));
        try
        {
            var encrypted = StorageCrypto.Encrypt(plain, _key, _storageId, EncryptedContentKind.SteamPasswords);
            AtomicFile.Write(FilePath, encrypted, true, Validate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        string? Validate(byte[] written)
        {
            try
            {
                var check = StorageCrypto.Decrypt(written, _key, _storageId, EncryptedContentKind.SteamPasswords);
                var ok = check.AsSpan().SequenceEqual(plain);
                CryptographicOperations.ZeroMemory(check);
                return ok ? null : "decrypted content differs";
            }
            catch (EnvelopeException ex)
            {
                return ex.Error.ToString();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _passwords.Clear();
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
