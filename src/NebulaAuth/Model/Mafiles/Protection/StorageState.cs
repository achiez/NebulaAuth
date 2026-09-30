using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NebulaAuth.Model.Mafiles.Protection;

public enum StorageMode
{
    /// <summary>Mafiles and backups are plain JSON / ZIP.</summary>
    Plain,

    /// <summary>Enabling encryption was started and is not finished yet (finished before the app opens).</summary>
    Encrypting,

    /// <summary>All accepted mafiles and backups are encrypted.</summary>
    Encrypted,

    /// <summary>Disabling encryption was started and is not finished yet (finished before the app opens).</summary>
    Decrypting
}

public enum StorageAccessError
{
    WrongPassword,
    CorruptedState,
    UnsupportedVersion,

    /// <summary>
    ///     storage-state.json does not exist, but the storage folders hold NebulaAuth encrypted files: the state was lost
    ///     (data folder restored / synced without it). Nothing is changed until the user restores it.
    /// </summary>
    StateMissing
}

/// <summary>
///     The storage can't be opened: wrong master password, damaged or missing storage-state.json, or a state written by
///     a newer version.
/// </summary>
public sealed class StorageAccessException(StorageAccessError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public StorageAccessError Error { get; } = error;
}

/// <summary>
///     Content of <c>storage-state.json</c> (next to settings.json): storage mode, format version and, when a key
///     exists, the storage id, KDF parameters with the salt and the data key wrapped by the master password key. Never
///     contains the master password, the plain data key or Steam passwords.
///     <code>
///     {
///       "formatVersion": 2,
///       "mode": "encrypted",
///       "storageId": "base64 (16 bytes)",
///       "kdf": { "id": "argon2id", "memoryKiB": 65536, "iterations": 8, "parallelism": 1, "salt": "base64 (16)" },
///       "keyWrap": "aes-256-gcm",
///       "wrappedKey": "base64 (nonce 12 | key 32 | tag 16)",
///       "keyChecksum": "base64 (SHA-256 of the key record)",
///       "mac": "base64 (HMAC-SHA256 of the state fields)"
///     }
///     </code>
///     <para>
///         <c>keyChecksum</c> (not keyed) tells a damaged key record from a wrong master password before the KDF runs.
///         <c>mac</c> (key derived from the data key) authenticates the mode, storage id, format version and KDF
///         parameters. It is checked right after the unlock, so a state edited outside the app (for example
///         "encrypted" changed to "decrypting") is rejected instead of being acted on. A plain state never has key
///         fields: a plain state with a key is damaged.
///     </para>
///     <para>
///         Version 1 (first development stage, never released: no checksum / MAC) is accepted only in the
///         <c>encrypted</c> mode and is rewritten as version 2 on the first successful unlock.
///     </para>
///     A missing file means a plain storage that was not upgraded to 2.x yet (old Steam passwords are still in mafiles),
///     unless the storage folders already hold encrypted files (<see cref="StorageAccessError.StateMissing" />).
/// </summary>
public sealed record StorageState
{
    public const string FILE_NAME = "storage-state.json";

    /// <summary>
    ///     Copy of the last state with a key (<c>storage-state.json.prev</c>), written before a state without the key
    ///     replaces it (encryption disabled): encrypted files that turn up later (restored backups) can still be opened.
    /// </summary>
    public const string PREVIOUS_SUFFIX = ".prev";

    public const string KEY_WRAP_ALG = "aes-256-gcm";

    /// <summary>Current version of the state file (independent of the encrypted file format).</summary>
    public const int CURRENT_VERSION = 2;

    /// <summary>First-stage state without checksum / MAC (never released; migrated on unlock).</summary>
    public const int LEGACY_VERSION = 1;

    // Accepted KDF parameters: a damaged or edited file must not make the unlock run for hours or use unbounded memory
    public const int MAX_KDF_MEMORY_KIB = 1024 * 1024;
    public const int MAX_KDF_ITERATIONS = 64;
    public const int MAX_KDF_PARALLELISM = 16;

    // The key wrap scheme did not change with the state file version: its associated data keeps "v1"
    private const int KEY_WRAP_VERSION = 1;

    public int FormatVersion { get; init; } = CURRENT_VERSION;
    public StorageMode Mode { get; init; }
    public byte[]? StorageId { get; init; }
    public KdfParameters? Kdf { get; init; }
    public byte[]? Salt { get; init; }
    public byte[]? WrappedKey { get; init; }

    /// <summary>SHA-256 of the key record (<see cref="ComputeKeyChecksum" />); version 2+.</summary>
    public byte[]? KeyChecksum { get; init; }

    /// <summary>HMAC-SHA256 of the state fields (<see cref="ComputeMac" />); version 2+.</summary>
    public byte[]? Mac { get; init; }

    /// <summary>
    ///     Plain storage only: the 1.x upgrade could not remove the legacy Steam password from some files (they could
    ///     not be read); it is repeated on the next starts until every file was processed.
    /// </summary>
    public bool PendingLegacyStrip { get; init; }

    public bool HasKey => StorageId != null && Kdf != null && Salt != null && WrappedKey != null;

    /// <summary>Written before version 2 (no checksum / MAC): rewritten on the first unlock.</summary>
    public bool IsLegacy => FormatVersion < CURRENT_VERSION;

    public static StorageState PlainState { get; } = new() {Mode = StorageMode.Plain};

    /// <summary>
    ///     Associated data of the key wrap: binds the storage id, the KDF and its salt to the wrapped key.
    /// </summary>
    public byte[] GetKeyWrapAad()
    {
        if (!HasKey) throw new InvalidOperationException("The storage has no key");
        var s =
            $"NebulaAuth/v{KEY_WRAP_VERSION}/keywrap|{Convert.ToBase64String(StorageId!)}|{Kdf!.Id}|{Kdf.MemoryKiB}|{Kdf.Iterations}|{Kdf.Parallelism}|{Convert.ToBase64String(Salt!)}";
        return Encoding.UTF8.GetBytes(s);
    }

    /// <summary>
    ///     Non-keyed checksum of the key record (storage id, KDF, salt, wrapped key): a damaged record is reported as a
    ///     damaged state, not as a wrong master password. It does not protect against deliberate edits (the MAC does).
    /// </summary>
    public byte[] ComputeKeyChecksum()
    {
        if (!HasKey) throw new InvalidOperationException("The storage has no key");
        var s =
            $"NebulaAuth/state-key|{Convert.ToBase64String(StorageId!)}|{Kdf!.Id}|{Kdf.MemoryKiB}|{Kdf.Iterations}|{Kdf.Parallelism}|{Convert.ToBase64String(Salt!)}|{Convert.ToBase64String(WrappedKey!)}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(s));
    }

    /// <summary>
    ///     Authentication code of the state: format version, mode, storage id and KDF parameters, keyed with
    ///     <paramref name="macKey" /> (<see cref="StorageCrypto.DeriveStateMacKey" />).
    /// </summary>
    public byte[] ComputeMac(byte[] macKey)
    {
        if (!HasKey) throw new InvalidOperationException("The storage has no key");
        var s =
            $"NebulaAuth/state|{FormatVersion}|{ModeToString(Mode)}|{Convert.ToBase64String(StorageId!)}|{Kdf!.Id}|{Kdf.MemoryKiB}|{Kdf.Iterations}|{Kdf.Parallelism}|{Convert.ToBase64String(Salt!)}";
        return HMACSHA256.HashData(macKey, Encoding.UTF8.GetBytes(s));
    }

    /// <summary>
    ///     The state as it is written: current version, key checksum and MAC (a state without a key has neither).
    /// </summary>
    public StorageState Seal(byte[]? macKey)
    {
        if (!HasKey) return this with {FormatVersion = CURRENT_VERSION, KeyChecksum = null, Mac = null};
        if (macKey == null) throw new InvalidOperationException("The storage is locked");
        var s = this with {FormatVersion = CURRENT_VERSION, PendingLegacyStrip = false};
        return s with {KeyChecksum = s.ComputeKeyChecksum(), Mac = s.ComputeMac(macKey)};
    }

    /// <summary>
    ///     Checks the MAC with the key of the unlocked storage. A legacy (version 1) state has no MAC.
    /// </summary>
    public bool VerifyMac(byte[] macKey)
    {
        if (!HasKey) return false;
        if (IsLegacy) return Mac == null && Mode == StorageMode.Encrypted;
        return Mac != null && CryptographicOperations.FixedTimeEquals(Mac, ComputeMac(macKey));
    }

    #region Serialization

    public byte[] ToJsonBytes()
    {
        var j = new JObject
        {
            ["formatVersion"] = FormatVersion,
            ["mode"] = ModeToString(Mode)
        };
        if (HasKey)
        {
            j["storageId"] = Convert.ToBase64String(StorageId!);
            j["kdf"] = new JObject
            {
                ["id"] = Kdf!.Id,
                ["memoryKiB"] = Kdf.MemoryKiB,
                ["iterations"] = Kdf.Iterations,
                ["parallelism"] = Kdf.Parallelism,
                ["salt"] = Convert.ToBase64String(Salt!)
            };
            j["keyWrap"] = KEY_WRAP_ALG;
            j["wrappedKey"] = Convert.ToBase64String(WrappedKey!);
            if (KeyChecksum != null) j["keyChecksum"] = Convert.ToBase64String(KeyChecksum);
            if (Mac != null) j["mac"] = Convert.ToBase64String(Mac);
        }
        else if (PendingLegacyStrip)
        {
            j["pendingLegacyStrip"] = true;
        }

        return new UTF8Encoding(false).GetBytes(j.ToString(Formatting.Indented));
    }

    /// <summary>
    ///     Reads the state file; null when it does not exist.
    /// </summary>
    /// <exception cref="StorageAccessException">Damaged file or unsupported version.</exception>
    public static StorageState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new StorageAccessException(StorageAccessError.CorruptedState,
                "The storage state file can't be read", ex);
        }

        return Parse(bytes);
    }

    /// <exception cref="StorageAccessException">Damaged content or unsupported version.</exception>
    public static StorageState Parse(byte[] bytes)
    {
        JObject j;
        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(bytes)))
            {
                DateParseHandling = DateParseHandling.None
            };
            j = JObject.Load(reader);
        }
        catch (Exception ex)
        {
            throw Corrupted("not a JSON object", ex);
        }

        var version = ReadInt(j, "formatVersion");
        if (version > CURRENT_VERSION)
        {
            throw new StorageAccessException(StorageAccessError.UnsupportedVersion,
                $"The storage format version {version} is not supported by this version of NebulaAuth");
        }

        if (version < LEGACY_VERSION) throw Corrupted("invalid format version");
        var mode = ParseMode(j.Value<string>("mode"));
        if (mode == StorageMode.Plain)
        {
            // A plain storage has no key: key fields next to "plain" mean a damaged or edited file (the key may be the
            // only way to read encrypted files that are still there)
            foreach (var name in new[] {"storageId", "kdf", "keyWrap", "wrappedKey", "keyChecksum", "mac"})
            {
                if (j[name] != null) throw Corrupted($"a plain storage has {name}");
            }

            var pending = j["pendingLegacyStrip"];
            if (pending != null && pending.Type != JTokenType.Boolean) throw Corrupted("pendingLegacyStrip is invalid");
            return new StorageState
            {
                FormatVersion = version,
                Mode = mode,
                PendingLegacyStrip = pending?.Value<bool>() == true
            };
        }

        // A first-stage state is migrated only from the final encrypted mode: without a MAC a transition mode can't be
        // trusted
        if (version == LEGACY_VERSION && mode != StorageMode.Encrypted)
            throw Corrupted($"a version {version} state can't be in the {ModeToString(mode)} mode");
        if (j["pendingLegacyStrip"] != null) throw Corrupted("an encrypted storage has pendingLegacyStrip");

        var kdfObj = j["kdf"] as JObject ?? throw Corrupted("kdf is missing");
        var kdfId = kdfObj.Value<string>("id") ?? throw Corrupted("kdf id is missing");
        if (kdfId != StorageCrypto.KDF_ARGON2ID)
        {
            throw new StorageAccessException(StorageAccessError.UnsupportedVersion,
                $"The key derivation function '{kdfId}' is not supported by this version of NebulaAuth");
        }

        var keyWrap = j.Value<string>("keyWrap");
        if (keyWrap != null && keyWrap != KEY_WRAP_ALG)
        {
            throw new StorageAccessException(StorageAccessError.UnsupportedVersion,
                $"The key wrap algorithm '{keyWrap}' is not supported by this version of NebulaAuth");
        }

        var kdf = new KdfParameters(kdfId, ReadInt(kdfObj, "memoryKiB"), ReadInt(kdfObj, "iterations"),
            ReadInt(kdfObj, "parallelism"));
        if (kdf.Parallelism is < 1 or > MAX_KDF_PARALLELISM || kdf.Iterations is < 1 or > MAX_KDF_ITERATIONS ||
            kdf.MemoryKiB < 8 * kdf.Parallelism || kdf.MemoryKiB > MAX_KDF_MEMORY_KIB)
        {
            throw Corrupted("invalid kdf parameters");
        }

        var state = new StorageState
        {
            FormatVersion = version,
            Mode = mode,
            StorageId = ReadBase64(j, "storageId", StorageCrypto.STORAGE_ID_SIZE),
            Kdf = kdf,
            Salt = ReadBase64(kdfObj, "salt", StorageCrypto.SALT_SIZE),
            WrappedKey = ReadBase64(j, "wrappedKey",
                StorageCrypto.NONCE_SIZE + StorageCrypto.KEY_SIZE + StorageCrypto.TAG_SIZE)
        };
        if (version == LEGACY_VERSION)
        {
            if (j["keyChecksum"] != null || j["mac"] != null) throw Corrupted("unexpected fields in a version 1 state");
            return state;
        }

        state = state with
        {
            KeyChecksum = ReadBase64(j, "keyChecksum", SHA256.HashSizeInBytes),
            Mac = ReadBase64(j, "mac", HMACSHA256.HashSizeInBytes)
        };
        if (!CryptographicOperations.FixedTimeEquals(state.KeyChecksum, state.ComputeKeyChecksum()))
            throw Corrupted("the key record does not match its checksum");
        return state;
    }

    private static int ReadInt(JObject j, string name)
    {
        if (j[name] is not {Type: JTokenType.Integer} token) throw Corrupted($"{name} is missing");
        try
        {
            return token.Value<int>();
        }
        catch (Exception ex)
        {
            throw Corrupted($"{name} is invalid", ex);
        }
    }

    private static byte[] ReadBase64(JObject j, string name, int length)
    {
        var s = j.Value<string>(name) ?? throw Corrupted($"{name} is missing");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(s);
        }
        catch (FormatException ex)
        {
            throw Corrupted($"{name} is not base64", ex);
        }

        if (bytes.Length != length) throw Corrupted($"{name} has invalid length");
        return bytes;
    }

    private static StorageMode ParseMode(string? s)
    {
        return s switch
        {
            "plain" => StorageMode.Plain,
            "encrypting" => StorageMode.Encrypting,
            "encrypted" => StorageMode.Encrypted,
            "decrypting" => StorageMode.Decrypting,
            _ => throw Corrupted("unknown mode")
        };
    }

    public static string ModeToString(StorageMode mode)
    {
        return mode switch
        {
            StorageMode.Plain => "plain",
            StorageMode.Encrypting => "encrypting",
            StorageMode.Encrypted => "encrypted",
            StorageMode.Decrypting => "decrypting",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public static StorageAccessException Corrupted(string what, Exception? inner = null)
    {
        return new StorageAccessException(StorageAccessError.CorruptedState,
            $"The storage state file is damaged: {what}", inner);
    }

    #endregion
}
