using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace NebulaAuth.Model.Mafiles.Protection;

/// <summary>
///     Kind of content in an encrypted file (header byte): an encrypted file is accepted only where this kind is expected
///     (a renamed file is detected by the header, not by its extension).
/// </summary>
public enum EncryptedContentKind : byte
{
    Mafile = 1,
    Zip = 2,
    SteamPasswords = 3,

    /// <summary>Achiefy device credential (nebula-device.encrypted).</summary>
    NebulaDevice = 4
}

public enum EnvelopeError
{
    None,

    /// <summary>No NebulaAuth magic: another program's format or not an encrypted file.</summary>
    NotNebulaFormat,

    /// <summary>NebulaAuth file of a newer (unknown) format version.</summary>
    UnsupportedVersion,

    /// <summary>The header describes other content than the file name says.</summary>
    KindMismatch,

    /// <summary>Encrypted by another storage (another data key / master password).</summary>
    ForeignStorage,

    /// <summary>Truncated file or failed integrity check (header, ciphertext or tag changed).</summary>
    Corrupted
}

public sealed class EnvelopeException(EnvelopeError error, string message) : Exception(message)
{
    public EnvelopeError Error { get; } = error;
}

/// <summary>
///     Header of an encrypted file (all integers are single bytes):
///     <code>
///     offset size
///     0      6    magic "NEBULA" (ASCII)
///     6      1    format version (1)
///     7      1    content kind (<see cref="EncryptedContentKind" />)
///     8      16   storage id (random, from storage-state.json)
///     24     12   AES-GCM nonce (random per write)
///     36     N    ciphertext
///     36+N   16   AES-GCM tag
///     </code>
///     The 36 header bytes are the associated data of AES-GCM: any change of magic, version, kind, storage id or nonce
///     fails the integrity check.
/// </summary>
public static class StorageCrypto
{
    public const int FORMAT_VERSION = 1;
    public const int KEY_SIZE = 32;
    public const int STORAGE_ID_SIZE = 16;
    public const int NONCE_SIZE = 12;
    public const int TAG_SIZE = 16;
    public const int SALT_SIZE = 16;
    public const int HEADER_SIZE = 6 + 1 + 1 + STORAGE_ID_SIZE + NONCE_SIZE;
    public const int MIN_FILE_SIZE = HEADER_SIZE + TAG_SIZE;

    public const string KDF_ARGON2ID = "argon2id";

    /// <summary>
    ///     Argon2id defaults for new keys (stored in the state file with the salt, so they can be raised later without
    ///     breaking existing storages). m = 64 MiB, t = 8, p = 1: about 0.5 s on a current desktop CPU with the managed
    ///     implementation; above the OWASP minimum (m = 19 MiB, t = 2, p = 1).
    /// </summary>
    public static KdfParameters DefaultKdf => new(KDF_ARGON2ID, 64 * 1024, 8, 1);

    private static readonly byte[] Magic = "NEBULA"u8.ToArray();
    private static readonly byte[] MafilesInfo = "NebulaAuth/v1/mafiles"u8.ToArray();
    private static readonly byte[] PasswordsInfo = "NebulaAuth/v1/steam-passwords"u8.ToArray();
    private static readonly byte[] StateInfo = "NebulaAuth/v1/state"u8.ToArray();
    private static readonly byte[] NebulaDeviceInfo = "NebulaAuth/v1/nebula-device"u8.ToArray();

    public static bool HasMagic(ReadOnlySpan<byte> data)
    {
        return data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);
    }

    #region Keys

    public static byte[] NewRandom(int size)
    {
        return RandomNumberGenerator.GetBytes(size);
    }

    /// <summary>
    ///     Key encryption key from the master password (Argon2id).
    /// </summary>
    public static byte[] DeriveKeyEncryptionKey(string password, byte[] salt, KdfParameters kdf)
    {
        if (kdf.Id != KDF_ARGON2ID) throw new NotSupportedException($"Unsupported KDF {kdf.Id}");
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(passwordBytes);
            argon.Salt = salt;
            argon.MemorySize = kdf.MemoryKiB;
            argon.Iterations = kdf.Iterations;
            argon.DegreeOfParallelism = kdf.Parallelism;
            return argon.GetBytes(KEY_SIZE);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    ///     Separate subkeys for mafiles (and backup ZIPs), for the Steam password store and for the Achiefy device
    ///     credential (HKDF-SHA256, salt = storage id).
    /// </summary>
    public static byte[] DeriveSubkey(byte[] dataKey, byte[] storageId, EncryptedContentKind kind)
    {
        var info = kind switch
        {
            EncryptedContentKind.SteamPasswords => PasswordsInfo,
            EncryptedContentKind.NebulaDevice => NebulaDeviceInfo,
            _ => MafilesInfo
        };
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, KEY_SIZE, storageId, info);
    }

    /// <summary>
    ///     Key of the authentication code of storage-state.json (<see cref="StorageState.Mac" />): HKDF-SHA256 of the
    ///     data key, salt = storage id, info "NebulaAuth/v1/state".
    /// </summary>
    public static byte[] DeriveStateMacKey(byte[] dataKey, byte[] storageId)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, KEY_SIZE, storageId, StateInfo);
    }

    /// <summary>
    ///     AES-256-GCM of the data key; <paramref name="aad" /> binds the state metadata (storage id, KDF, salt).
    ///     Result: nonce | ciphertext | tag.
    /// </summary>
    public static byte[] WrapKey(byte[] kek, byte[] dataKey, byte[] aad)
    {
        var result = new byte[NONCE_SIZE + dataKey.Length + TAG_SIZE];
        var nonce = result.AsSpan(0, NONCE_SIZE);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(kek, TAG_SIZE);
        gcm.Encrypt(nonce, dataKey, result.AsSpan(NONCE_SIZE, dataKey.Length), result.AsSpan(^TAG_SIZE), aad);
        return result;
    }

    /// <summary>
    ///     Returns the data key, or null when the tag does not match (wrong password or changed state).
    /// </summary>
    public static byte[]? UnwrapKey(byte[] kek, byte[] wrapped, byte[] aad)
    {
        if (wrapped.Length != NONCE_SIZE + KEY_SIZE + TAG_SIZE) return null;
        var key = new byte[KEY_SIZE];
        try
        {
            using var gcm = new AesGcm(kek, TAG_SIZE);
            gcm.Decrypt(wrapped.AsSpan(0, NONCE_SIZE), wrapped.AsSpan(NONCE_SIZE, KEY_SIZE), wrapped.AsSpan(^TAG_SIZE),
                key, aad);
            return key;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
    }

    #endregion

    #region Envelope

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] key, byte[] storageId, EncryptedContentKind kind)
    {
        var result = new byte[HEADER_SIZE + plaintext.Length + TAG_SIZE];
        var span = result.AsSpan();
        Magic.CopyTo(span);
        span[6] = FORMAT_VERSION;
        span[7] = (byte) kind;
        storageId.CopyTo(span.Slice(8, STORAGE_ID_SIZE));
        var nonce = span.Slice(8 + STORAGE_ID_SIZE, NONCE_SIZE);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TAG_SIZE);
        gcm.Encrypt(nonce, plaintext, span.Slice(HEADER_SIZE, plaintext.Length), span[^TAG_SIZE..],
            span[..HEADER_SIZE]);
        return result;
    }

    /// <summary>
    ///     Checks the header without decrypting.
    /// </summary>
    public static EnvelopeError CheckHeader(ReadOnlySpan<byte> data, byte[] storageId, EncryptedContentKind kind)
    {
        if (!HasMagic(data)) return EnvelopeError.NotNebulaFormat;
        if (data.Length < 7) return EnvelopeError.Corrupted;
        if (data[6] != FORMAT_VERSION) return EnvelopeError.UnsupportedVersion;
        if (data.Length < MIN_FILE_SIZE) return EnvelopeError.Corrupted;
        if (data[7] != (byte) kind) return EnvelopeError.KindMismatch;
        if (!data.Slice(8, STORAGE_ID_SIZE).SequenceEqual(storageId)) return EnvelopeError.ForeignStorage;
        return EnvelopeError.None;
    }

    /// <exception cref="EnvelopeException">Not decryptable with this storage key.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> data, byte[] key, byte[] storageId, EncryptedContentKind kind)
    {
        var headerError = CheckHeader(data, storageId, kind);
        if (headerError != EnvelopeError.None)
        {
            throw new EnvelopeException(headerError, $"Encrypted file header check failed: {headerError}");
        }

        var cipherLength = data.Length - HEADER_SIZE - TAG_SIZE;
        var plaintext = new byte[cipherLength];
        try
        {
            using var gcm = new AesGcm(key, TAG_SIZE);
            gcm.Decrypt(data.Slice(8 + STORAGE_ID_SIZE, NONCE_SIZE), data.Slice(HEADER_SIZE, cipherLength),
                data[^TAG_SIZE..], plaintext, data[..HEADER_SIZE]);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new EnvelopeException(EnvelopeError.Corrupted, "Integrity check of the encrypted file failed");
        }
    }

    #endregion
}

public sealed record KdfParameters(string Id, int MemoryKiB, int Iterations, int Parallelism);
