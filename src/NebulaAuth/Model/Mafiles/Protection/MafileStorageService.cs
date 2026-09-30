using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.Model.Exceptions;

namespace NebulaAuth.Model.Mafiles.Protection;

/// <summary>
///     A storage file can't be used in the current mode (see <see cref="Reason" />).
/// </summary>
public sealed class StorageFileException(SkippedFileReason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SkippedFileReason Reason { get; } = reason;
}

/// <summary>
///     A mafile of the storage folder: physical path and logical name (<see cref="Entities.Mafile.Filename" />).
/// </summary>
public sealed record AccountFile(string Path, string LogicalName, bool Encrypted);

public enum RenameAccountFileResult
{
    Renamed,
    SourceMissing,
    TargetExists
}

/// <summary>
///     The storage layer: owns the storage mode (<c>storage-state.json</c>), the keys, the format of every mafile /
///     backup / backup ZIP and all transitions between plain and encrypted storage. Every file is published through
///     <see cref="AtomicFile" />. <see cref="Storage" /> and the rest of the app read and write mafiles only through it.
///     <para>Startup order: <see cref="LoadState" /> → <see cref="Unlock" /> (when <see cref="RequiresUnlock" />) →
///     <see cref="PrepareAsync" /> → <see cref="Storage.Initialize" />.</para>
/// </summary>
public sealed class MafileStorageService
{
    private static MafileStorageService? _current;

    /// <summary>
    ///     The storage of the app (data folder). Replaced by tests.
    /// </summary>
    public static MafileStorageService Current
    {
        get => _current ??= new MafileStorageService(StorageLayout.FromDataRoot());
        set => _current = value;
    }

    /// <summary>Infix of a damaged Steam password store moved aside: <c>steam-passwords.encrypted.damaged-&lt;time&gt;</c>.</summary>
    public const string DAMAGED_INFIX = ".damaged-";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _skippedLock = new();
    private StorageKeys? _keys;
    private SteamPasswordStore? _passwords;
    private List<SkippedFile> _skipped = [];
    private StorageState? _state;
    private bool _stateLoaded;

    public StorageLayout Layout { get; }

    public MafileStorageService(StorageLayout layout)
    {
        Layout = layout;
    }

    #region State

    public StorageMode Mode => _state?.Mode ?? StorageMode.Plain;

    /// <summary>
    ///     First 2.x start: no state file yet, legacy Steam passwords may still be in mafiles.
    /// </summary>
    public bool NeedsUpgrade => _stateLoaded && _state == null;

    /// <summary>
    ///     The master password is needed before the accounts can be loaded.
    /// </summary>
    public bool RequiresUnlock => Mode != StorageMode.Plain;

    public bool IsUnlocked => _keys != null;

    public bool IsEncrypted => Mode == StorageMode.Encrypted;

    /// <summary>
    ///     Steam passwords; only in an encrypted, unlocked storage (null otherwise).
    /// </summary>
    public SteamPasswordStore? Passwords => Mode == StorageMode.Encrypted ? _passwords : null;

    /// <summary>
    ///     Files that were not loaded or not converted to the current mode (skipped-files screen).
    /// </summary>
    public IReadOnlyList<SkippedFile> SkippedFiles
    {
        get
        {
            lock (_skippedLock) return _skipped.ToList();
        }
    }

    /// <summary>
    ///     <see cref="SkippedFiles" /> changed (raised on the thread that changed it, outside the internal lock).
    /// </summary>
    public event Action? SkippedFilesChanged;

    /// <summary>
    ///     Reads storage-state.json.
    /// </summary>
    /// <exception cref="StorageAccessException">
    ///     Damaged state, unsupported version, or the state is missing while the storage folders hold encrypted files
    ///     (<see cref="StorageAccessError.StateMissing" />: nothing is changed, the user restores the state first).
    /// </exception>
    public void LoadState()
    {
        var state = StorageState.Load(Layout.StateFile);
        if (state == null)
        {
            // A missing state is taken as a plain 1.x storage only when nothing is encrypted: otherwise writing a new
            // plain state would make the encrypted files unreadable for good (and a later "enable" would make them
            // foreign)
            var encrypted = FindEncryptedFiles(true);
            if (encrypted.Count > 0)
            {
                var prev = File.Exists(Layout.PreviousStateFile)
                    ? $" A copy of an earlier state exists: {Path.GetFileName(Layout.PreviousStateFile)}."
                    : string.Empty;
                throw new StorageAccessException(StorageAccessError.StateMissing,
                    $"{StorageState.FILE_NAME} is missing, but {encrypted.Count} NebulaAuth encrypted file(s) are in the data folder (for example {Path.GetFileName(encrypted[0])}).{prev}");
            }
        }

        _state = state;
        _stateLoaded = true;
    }

    private void EnsureStateLoaded()
    {
        if (!_stateLoaded) LoadState();
    }

    /// <summary>
    ///     Publishes the state (sealed: key checksum and MAC with the key of the unlocked storage).
    /// </summary>
    private void SaveState(StorageState state)
    {
        byte[]? macKey = null;
        if (state.HasKey)
        {
            var keys = RequireKeys();
            if (!keys.StorageId.AsSpan().SequenceEqual(state.StorageId))
                throw new InvalidOperationException("The state belongs to another storage key");
            macKey = keys.StateMacKey;
        }

        var sealedState = state.Seal(macKey);
        AtomicFile.Write(Layout.StateFile, sealedState.ToJsonBytes(), true, StateValidator(macKey));
        _state = sealedState;
    }

    /// <summary>
    ///     Keeps the current state with its key as storage-state.json.prev before a state without a key replaces it.
    /// </summary>
    private void SavePreviousState()
    {
        var state = _state;
        if (state is not {HasKey: true}) return;
        AtomicFile.Write(Layout.PreviousStateFile, state.ToJsonBytes(), true, StateValidator(_keys?.StateMacKey));
    }

    private static AtomicWriteValidator StateValidator(byte[]? macKey)
    {
        return bytes =>
        {
            try
            {
                var parsed = StorageState.Parse(bytes);
                return macKey == null || parsed.VerifyMac(macKey) ? null : "state MAC does not match";
            }
            catch (StorageAccessException ex)
            {
                return ex.Error.ToString();
            }
        };
    }

    /// <summary>
    ///     Files of the storage folders (any name) whose content starts with the NebulaAuth encrypted file marker.
    ///     Files that can't be read are not included.
    /// </summary>
    private List<string> FindEncryptedFiles(bool includePasswordStore)
    {
        var result = new List<string>();
        var candidates = Layout.AccountDirectories.Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d).OrderBy(p => p, StringComparer.Ordinal)).ToList();
        if (includePasswordStore && File.Exists(Layout.PasswordsFile)) candidates.Add(Layout.PasswordsFile);
        foreach (var path in candidates)
        {
            try
            {
                if (StorageCrypto.HasMagic(ReadHead(path))) result.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Checked again where it matters (a locked file blocks disabling encryption)
            }
        }

        return result;
    }

    /// <summary>
    ///     Number of NebulaAuth encrypted files in the storage folders. In a plain storage they can't be read (no key);
    ///     enabling encryption creates a new key, so they would stay unreadable ("another storage"): the UI asks the user
    ///     to confirm first.
    /// </summary>
    public int CountEncryptedFiles()
    {
        return FindEncryptedFiles(false).Count;
    }

    #endregion

    #region Unlock / keys

    /// <summary>
    ///     Checks the master password against the wrapped data key and keeps the keys in memory. Slow (Argon2id): call
    ///     from a background thread.
    /// </summary>
    /// <exception cref="StorageAccessException">Wrong password, damaged state or unsupported version.</exception>
    public void Unlock(string password)
    {
        EnsureStateLoaded();
        var state = _state;
        if (state is not {HasKey: true})
            throw new InvalidOperationException("The storage is not encrypted");
        var dataKey = UnwrapDataKey(state, password);
        try
        {
            // The state fields (mode above all) are acted on only when they were written by this storage
            var macKey = StorageCrypto.DeriveStateMacKey(dataKey, state.StorageId!);
            try
            {
                if (!state.VerifyMac(macKey))
                {
                    throw new StorageAccessException(StorageAccessError.CorruptedState,
                        "The storage state file was changed outside NebulaAuth: its authentication code does not match");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(macKey);
            }

            SetKeys(dataKey, state.StorageId!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        if (state.IsLegacy) MigrateLegacyState(state);
    }

    /// <summary>
    ///     First-stage state (version 1, no checksum / MAC): rewritten as the current version. A failed write is retried on
    ///     the next unlock; the storage works meanwhile.
    /// </summary>
    private void MigrateLegacyState(StorageState state)
    {
        try
        {
            SaveState(state);
            Shell.Logger.Info("Storage state upgraded to version {version}", StorageState.CURRENT_VERSION);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shell.Logger.Warn(ex, "Storage state can't be upgraded now; retried on the next start");
        }
    }

    private static byte[] UnwrapDataKey(StorageState state, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new StorageAccessException(StorageAccessError.WrongPassword, "The master password is empty");
        var kek = StorageCrypto.DeriveKeyEncryptionKey(password, state.Salt!, state.Kdf!);
        try
        {
            return StorageCrypto.UnwrapKey(kek, state.WrappedKey!, state.GetKeyWrapAad()) ??
                   throw new StorageAccessException(StorageAccessError.WrongPassword, "Wrong master password");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private void SetKeys(byte[] dataKey, byte[] storageId)
    {
        var old = _keys;
        _keys = new StorageKeys(storageId.ToArray(),
            StorageCrypto.DeriveSubkey(dataKey, storageId, EncryptedContentKind.Mafile),
            StorageCrypto.DeriveSubkey(dataKey, storageId, EncryptedContentKind.SteamPasswords),
            StorageCrypto.DeriveStateMacKey(dataKey, storageId));
        old?.Dispose();
    }

    /// <summary>
    ///     Forgets the keys (zeroed) and closes the password store.
    /// </summary>
    public void Lock()
    {
        _passwords?.Dispose();
        _passwords = null;
        _keys?.Dispose();
        _keys = null;
    }

    private StorageKeys RequireKeys()
    {
        return _keys ?? throw new InvalidOperationException("The storage is locked");
    }

    /// <summary>
    ///     Changes the master password: only the wrapped data key in storage-state.json is replaced (atomically), no
    ///     mafile is rewritten. Slow (two Argon2id runs): call from a background thread.
    /// </summary>
    /// <exception cref="StorageAccessException">The current password is wrong.</exception>
    public async Task ChangeMasterPasswordAsync(string currentPassword, string newPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = _state;
            if (state is not {HasKey: true, Mode: StorageMode.Encrypted} || _keys == null)
                throw new InvalidOperationException("The storage is not encrypted or is locked");
            var dataKey = UnwrapDataKey(state, currentPassword);
            try
            {
                var check = StorageCrypto.DeriveSubkey(dataKey, state.StorageId!, EncryptedContentKind.Mafile);
                var same = CryptographicOperations.FixedTimeEquals(check, _keys.MafileKey);
                CryptographicOperations.ZeroMemory(check);
                if (!same) throw new StorageAccessException(StorageAccessError.WrongPassword, "Wrong master password");
                SaveState(WrapNewKey(state, dataKey, newPassword));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static StorageState WrapNewKey(StorageState state, byte[] dataKey, string password)
    {
        var kdf = StorageCrypto.DefaultKdf;
        var withKdf = state with
        {
            Kdf = kdf,
            Salt = StorageCrypto.NewRandom(StorageCrypto.SALT_SIZE),
            WrappedKey = new byte[StorageCrypto.NONCE_SIZE + StorageCrypto.KEY_SIZE + StorageCrypto.TAG_SIZE]
        };
        var kek = StorageCrypto.DeriveKeyEncryptionKey(password, withKdf.Salt!, kdf);
        try
        {
            return withKdf with {WrappedKey = StorageCrypto.WrapKey(kek, dataKey, withKdf.GetKeyWrapAad())};
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    #endregion

    #region Startup

    /// <summary>
    ///     Startup reconciliation, before <see cref="Storage.Initialize" />: removes own temporary files whose target is
    ///     intact, runs the 1.x upgrade (legacy Steam passwords removed from all mafiles and backup ZIPs), finishes an
    ///     interrupted enable / disable and brings plain files added manually to an encrypted storage to the encrypted
    ///     format. Problem files stay in place and are listed in <see cref="SkippedFiles" />.
    /// </summary>
    public async Task<StorageOperationResult> PrepareAsync(IProgress<StorageProgress>? progress = null,
        CancellationToken token = default)
    {
        EnsureStateLoaded();
        if (RequiresUnlock && !IsUnlocked) throw new InvalidOperationException("The storage is locked");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                lock (_skippedLock) _skipped = [];
                var result = new StorageOperationResult();
                foreach (var dir in Layout.AccountDirectories) Directory.CreateDirectory(dir);
                CleanupTemps(result);
                switch (Mode)
                {
                    case StorageMode.Plain when _state is null or {PendingLegacyStrip: true}:
                    {
                        // First 2.x start, or files that could not be read by an earlier upgrade run
                        var unreadable = UpgradeLegacy(result, progress, token);
                        var pending = unreadable > 0;
                        if (_state == null || _state.PendingLegacyStrip != pending)
                            SaveState(StorageState.PlainState with {PendingLegacyStrip = pending});
                        result.Completed = true;
                        break;
                    }
                    case StorageMode.Plain:
                        ScanPlain(result);
                        result.Completed = true;
                        break;
                    case StorageMode.Encrypted:
                        ConvertAll(true, result, progress, token);
                        result.Completed = true;
                        break;
                    case StorageMode.Encrypting:
                    case StorageMode.Decrypting:
                        FinishTransition(result, progress, token);
                        break;
                }

                OpenPasswordStore(result);
                result.ModeAfter = Mode;
                ReportSkipped(result.AllSkipped);
                SkippedFilesChanged?.Invoke();
                LogResult("Storage prepared", result);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OpenPasswordStore(StorageOperationResult result)
    {
        if (Mode != StorageMode.Encrypted || _keys == null || _passwords != null) return;
        var path = Layout.PasswordsFile;
        try
        {
            _passwords = SteamPasswordStore.Open(path, _keys.PasswordsKey.ToArray(), _keys.StorageId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked / no access: the file is probably fine. It is not touched; this session runs without saved Steam
            // passwords (a new store would overwrite it on the first change)
            Shell.Logger.Error(ex, "Steam password store can't be read now; saved Steam passwords are not available");
            result.Skipped.Add(new SkippedFile(Path.GetFileName(path), path, SkippedFileReason.Unreadable, false,
                "Saved Steam passwords are not available until the next start: " + ex.Message));
        }
        catch (Exception ex) when (ex is EnvelopeException or FormatException)
        {
            // Damaged content. Never overwritten: moved aside, the user may still recover it; a new empty store is used
            var reason = ex is EnvelopeException env ? MapEnvelopeError(env.Error) : SkippedFileReason.Corrupted;
            var aside = path + $"{DAMAGED_INFIX}{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(path, aside);
                Shell.Logger.Error(ex, "Steam password store can't be opened, moved to {file}",
                    Path.GetFileName(aside));
                result.Skipped.Add(new SkippedFile(Path.GetFileName(aside), aside, reason, false, ex.Message));
                _passwords = SteamPasswordStore.Open(path, _keys.PasswordsKey.ToArray(), _keys.StorageId);
            }
            catch (Exception moveEx)
            {
                Shell.Logger.Error(moveEx, "Steam password store can't be opened or moved aside");
                result.Skipped.Add(new SkippedFile(Path.GetFileName(path), path, reason, false, ex.Message));
            }
        }
    }

    #endregion

    #region Enable / disable

    /// <summary>
    ///     Enables encryption: creates a random data key protected by <paramref name="masterPassword" />, stores the
    ///     state <see cref="StorageMode.Encrypting" /> and converts every mafile / backup / backup ZIP (temporary file,
    ///     verification, old file deleted last). The mode becomes <see cref="StorageMode.Encrypted" /> only after the
    ///     whole set is processed without failures; otherwise the operation is resumed by
    ///     <see cref="ResumeTransitionAsync" /> or on the next start. Mafile writes wait until the operation ends.
    /// </summary>
    /// <param name="masterPassword">The new master password.</param>
    /// <param name="progress">Progress of the conversion.</param>
    /// <param name="token">Cancels the operation (it stays resumable).</param>
    /// <param name="acceptEncryptedFiles">
    ///     The user confirmed that NebulaAuth encrypted files already in the plain storage (<see cref="CountEncryptedFiles" />)
    ///     stay unreadable: the new key can't open them. Without it such files make the call fail.
    /// </param>
    public async Task<StorageOperationResult> EnableEncryptionAsync(string masterPassword,
        IProgress<StorageProgress>? progress = null, CancellationToken token = default,
        bool acceptEncryptedFiles = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        EnsureStateLoaded();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_state is not {Mode: StorageMode.Plain})
                throw new InvalidOperationException("Encryption can be enabled only for a prepared plain storage");
            return await Task.Run(() =>
            {
                var encryptedFiles = FindEncryptedFiles(false);
                if (encryptedFiles.Count > 0 && !acceptEncryptedFiles)
                {
                    throw new InvalidOperationException(
                        $"{encryptedFiles.Count} encrypted file(s) of another storage key are in the storage folders; the user must confirm first");
                }

                var storageId = StorageCrypto.NewRandom(StorageCrypto.STORAGE_ID_SIZE);
                var dataKey = StorageCrypto.NewRandom(StorageCrypto.KEY_SIZE);
                try
                {
                    // The keys are needed to seal (MAC) the new state
                    SetKeys(dataKey, storageId);
                    var state = WrapNewKey(new StorageState
                    {
                        Mode = StorageMode.Encrypting,
                        StorageId = storageId
                    }, dataKey, masterPassword);
                    SaveState(state);
                }
                catch
                {
                    Lock();
                    throw;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dataKey);
                }

                StorageFaults.Point(StorageFaults.TRANSITION_STATE_SAVED, Layout.StateFile);
                var result = new StorageOperationResult();
                CleanupTemps(result);
                FinishTransition(result, progress, token);
                OpenPasswordStore(result);
                result.ModeAfter = Mode;
                MergeSkipped(result);
                LogResult("Enable encryption", result);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Disables encryption: state <see cref="StorageMode.Decrypting" />, every own encrypted file is decrypted
    ///     (temporary file, verification, encrypted file deleted last). Only when no file encrypted with this storage is
    ///     left in the storage folders (<see cref="BlockOnOwnEncryptedFiles" />) the Steam password store is deleted,
    ///     the state with the key is kept as storage-state.json.prev and the mode becomes <see cref="StorageMode.Plain" />.
    ///     Otherwise the mode stays <see cref="StorageMode.Decrypting" /> with the key and the files are listed in
    ///     <see cref="StorageOperationResult.Failed" />.
    /// </summary>
    /// <param name="progress">Progress of the conversion.</param>
    /// <param name="token">Cancels the operation (it stays resumable).</param>
    /// <param name="currentPassword">
    ///     When given, it must be the current master password (checked before anything changes).
    /// </param>
    /// <exception cref="StorageAccessException">
    ///     <paramref name="currentPassword" /> is wrong (<see cref="StorageAccessError.WrongPassword" />).
    /// </exception>
    public async Task<StorageOperationResult> DisableEncryptionAsync(IProgress<StorageProgress>? progress = null,
        CancellationToken token = default, string? currentPassword = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_state is not {Mode: StorageMode.Encrypted} || _keys == null)
                throw new InvalidOperationException("The storage is not encrypted or is locked");
            return await Task.Run(() =>
            {
                if (currentPassword != null) CheckCurrentPassword(_state, currentPassword);
                SaveState(_state with {Mode = StorageMode.Decrypting});
                StorageFaults.Point(StorageFaults.TRANSITION_STATE_SAVED, Layout.StateFile);
                var result = new StorageOperationResult();
                CleanupTemps(result);
                FinishTransition(result, progress, token);
                result.ModeAfter = Mode;
                MergeSkipped(result);
                LogResult("Disable encryption", result);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <exception cref="StorageAccessException">The password does not open the current key.</exception>
    private void CheckCurrentPassword(StorageState state, string password)
    {
        var dataKey = UnwrapDataKey(state, password);
        try
        {
            var check = StorageCrypto.DeriveSubkey(dataKey, state.StorageId!, EncryptedContentKind.Mafile);
            var same = CryptographicOperations.FixedTimeEquals(check, RequireKeys().MafileKey);
            CryptographicOperations.ZeroMemory(check);
            if (!same) throw new StorageAccessException(StorageAccessError.WrongPassword, "Wrong master password");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    ///     Continues an unfinished enable / disable (state <see cref="StorageMode.Encrypting" /> /
    ///     <see cref="StorageMode.Decrypting" />), e.g. after a failure reported by the previous attempt.
    /// </summary>
    public async Task<StorageOperationResult> ResumeTransitionAsync(IProgress<StorageProgress>? progress = null,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Mode is not (StorageMode.Encrypting or StorageMode.Decrypting))
                throw new InvalidOperationException("No storage mode change is in progress");
            RequireKeys();
            return await Task.Run(() =>
            {
                var result = new StorageOperationResult();
                CleanupTemps(result);
                FinishTransition(result, progress, token);
                OpenPasswordStore(result);
                result.ModeAfter = Mode;
                MergeSkipped(result);
                LogResult("Resume storage mode change", result);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Runs under the gate
    private void FinishTransition(StorageOperationResult result, IProgress<StorageProgress>? progress,
        CancellationToken token)
    {
        var state = _state!;
        var encrypt = state.Mode == StorageMode.Encrypting;
        ConvertAll(encrypt, result, progress, token);
        // The key is dropped only when nothing needs it any more
        if (!encrypt) BlockOnOwnEncryptedFiles(result);
        if (result.Failed.Count > 0)
        {
            Shell.Logger.Warn("Storage mode change is not finished: {count} file(s) failed", result.Failed.Count);
            return;
        }

        if (encrypt)
        {
            StorageFaults.Point(StorageFaults.TRANSITION_BEFORE_FINAL_STATE, Layout.StateFile);
            SaveState(state with {Mode = StorageMode.Encrypted});
        }
        else
        {
            // Encrypted files that turn up later (restored backups) can still be opened with this copy
            SavePreviousState();
            StorageFaults.Point(StorageFaults.TRANSITION_BEFORE_PASSWORDS_DELETE, Layout.PasswordsFile);
            _passwords?.Dispose();
            _passwords = null;
            DeletePasswordStoreFiles();
            StorageFaults.Point(StorageFaults.TRANSITION_BEFORE_FINAL_STATE, Layout.StateFile);
            SaveState(StorageState.PlainState);
            Lock();
        }

        result.Completed = true;
    }

    /// <summary>
    ///     Deletes the saved Steam passwords (the user turned saving off): the open store is emptied, then its file and
    ///     copies of a damaged store are removed. The storage mode and the key do not change; a later save creates a new
    ///     store file.
    /// </summary>
    /// <returns>Number of passwords that were in the open store.</returns>
    /// <exception cref="IOException">The store file can't be written or deleted (the passwords may still be there).</exception>
    /// <exception cref="UnauthorizedAccessException">No access to the store file.</exception>
    public int ClearSteamPasswords()
    {
        _gate.Wait();
        try
        {
            var store = Passwords;
            var count = store?.Count ?? 0;
            store?.Clear();
            DeletePasswordStoreFiles();
            Shell.Logger.Info("Saved Steam passwords deleted ({count})", count);
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     The Steam password store and copies of a damaged store moved aside (<c>*.damaged-*</c>).
    /// </summary>
    private void DeletePasswordStoreFiles()
    {
        if (File.Exists(Layout.PasswordsFile)) File.Delete(Layout.PasswordsFile);
        var folder = Path.GetDirectoryName(Layout.PasswordsFile);
        if (folder == null || !Directory.Exists(folder)) return;
        foreach (var damaged in Directory.GetFiles(folder, Path.GetFileName(Layout.PasswordsFile) + DAMAGED_INFIX + "*"))
        {
            File.Delete(damaged);
        }
    }

    /// <summary>
    ///     Disabling encryption: any file of the storage folders that is still encrypted with this storage (not
    ///     decrypted: locked, conflict with a differing plain copy, renamed, damaged, wrong content kind, unfinished
    ///     write) or that can't be read to check it keeps the storage in <see cref="StorageMode.Decrypting" /> with its
    ///     key: it is moved to <see cref="StorageOperationResult.Failed" />. Without the key such a file would be lost.
    ///     Files of other storages (other id) and other formats don't need the key and are only reported.
    /// </summary>
    private void BlockOnOwnEncryptedFiles(StorageOperationResult result)
    {
        var storageId = RequireKeys().StorageId;
        foreach (var dir in Layout.AccountDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.GetFiles(dir).OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                var kind = StorageFileNames.Classify(name);
                byte[] head;
                try
                {
                    head = ReadHead(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Can't tell whether it is still encrypted with this key: keep the key. Files that are not storage
                    // files (notes, thumbnails...) are not waited for
                    if (kind != StorageFileKind.Other)
                        Block(path, SkippedFileReason.Unreadable, "Can't be checked: " + ex.Message);
                    continue;
                }

                if (!StorageCrypto.HasMagic(head)) continue;
                // Too short to have a storage id: a damaged file that may be ours
                const int idEnd = 8 + StorageCrypto.STORAGE_ID_SIZE;
                if (head.Length >= idEnd && !head.AsSpan(8, StorageCrypto.STORAGE_ID_SIZE).SequenceEqual(storageId))
                    continue;
                Block(path, kind switch
                {
                    StorageFileKind.Temp => SkippedFileReason.UnfinishedWrite,
                    StorageFileKind.EncryptedMafile or StorageFileKind.EncryptedZip => SkippedFileReason.Corrupted,
                    _ => SkippedFileReason.ExtensionMismatch
                }, "Encrypted with this storage key and not decrypted");
            }
        }

        void Block(string path, SkippedFileReason reason, string details)
        {
            if (result.Failed.Any(f => string.Equals(f.Path, path, StringComparison.Ordinal))) return;
            var reported = result.Skipped.FindIndex(f => string.Equals(f.Path, path, StringComparison.Ordinal));
            if (reported >= 0)
            {
                // Keep the reason found by the conversion (conflict, damaged...)
                result.Failed.Add(result.Skipped[reported]);
                result.Skipped.RemoveAt(reported);
                return;
            }

            result.Failed.Add(new SkippedFile(Path.GetFileName(path), path, reason, false, details));
        }
    }

    #endregion

    #region Bulk conversion

    private IEnumerable<(string Dir, string Path, StorageFileKind Kind)> EnumerateStorageFiles()
    {
        foreach (var dir in Layout.AccountDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            var isBackup = dir == Layout.BackupDirectory;
            foreach (var path in Directory.GetFiles(dir).OrderBy(p => p, StringComparer.Ordinal))
            {
                var kind = StorageFileNames.Classify(Path.GetFileName(path));
                if (kind is StorageFileKind.Other or StorageFileKind.Temp) continue;
                if (!isBackup && kind is StorageFileKind.PlainZip or StorageFileKind.EncryptedZip) continue;
                yield return (dir, path, kind);
            }
        }
    }

    // Runs under the gate. toEncrypted: plain → encrypted (also used in the encrypted mode for manually added files)
    private void ConvertAll(bool toEncrypted, StorageOperationResult result, IProgress<StorageProgress>? progress,
        CancellationToken token)
    {
        var keys = RequireKeys();
        var files = EnumerateStorageFiles().ToList();
        result.Total += files.Count;
        var processed = 0;
        foreach (var (dir, path, kind) in files)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new StorageProgress(processed++, files.Count, Path.GetFileName(path)));
            if (!File.Exists(path)) continue; // removed as a duplicate of an already processed file
            var isSource = toEncrypted ? StorageFileNames.IsPlain(kind) : StorageFileNames.IsEncrypted(kind);
            try
            {
                if (isSource)
                {
                    ConvertFile(dir, path, kind, toEncrypted, keys, result);
                }
                else
                {
                    var problem = CheckTargetFormatFile(path, kind, keys);
                    if (problem != null) result.Skipped.Add(problem);
                }
            }
            catch (Exception ex) when (!StorageFaults.IsSimulatedCrash(ex) && ex is not OperationCanceledException)
            {
                Shell.Logger.Error(ex, "Storage: can't convert {file}", Path.GetFileName(path));
                result.Failed.Add(new SkippedFile(Path.GetFileName(path), path, SkippedFileReason.ConversionFailed,
                    StorageFileNames.IsPlain(kind), ex.Message));
            }
        }

        progress?.Report(new StorageProgress(files.Count, files.Count, null));
    }

    private void ConvertFile(string dir, string path, StorageFileKind kind, bool toEncrypted, StorageKeys keys,
        StorageOperationResult result)
    {
        var buffers = new List<byte[]>();
        try
        {
            ConvertFileCore(dir, path, kind, toEncrypted, keys, result, buffers);
        }
        finally
        {
            // Plain mafile content (secrets) is not kept in memory longer than needed
            foreach (var buffer in buffers) CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private void ConvertFileCore(string dir, string path, StorageFileKind kind, bool toEncrypted, StorageKeys keys,
        StorageOperationResult result, List<byte[]> buffers)
    {
        var name = Path.GetFileName(path);
        var contentKind = StorageFileNames.ContentKind(kind);
        byte[] source;
        try
        {
            source = File.ReadAllBytes(path);
            buffers.Add(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Skipped.Add(new SkippedFile(name, path, SkippedFileReason.Unreadable, toEncrypted,
                ex.Message));
            return;
        }

        byte[] plaintext;
        if (toEncrypted)
        {
            if (StorageCrypto.HasMagic(source))
            {
                result.Skipped.Add(new SkippedFile(name, path, SkippedFileReason.ExtensionMismatch, false,
                    "Encrypted content in a file with a plain extension"));
                return;
            }

            var prepared = PreparePlain(dir, name, kind, source, out var problem);
            if (prepared == null)
            {
                result.Skipped.Add(problem!);
                return;
            }

            plaintext = prepared;
            buffers.Add(plaintext);
        }
        else
        {
            try
            {
                plaintext = StorageCrypto.Decrypt(source, keys.MafileKey, keys.StorageId, contentKind);
                buffers.Add(plaintext);
            }
            catch (EnvelopeException ex)
            {
                result.Skipped.Add(new SkippedFile(name, path, MapEnvelopeError(ex.Error), false, ex.Message));
                return;
            }
        }

        var targetName = StorageFileNames.ToName(name, toEncrypted);
        var targetPath = Path.Combine(dir, targetName);
        // Exact name only: on a case-sensitive file system "ACC.mafile.encrypted" is not the counterpart of
        // "acc.mafile" (on Windows / macOS the file system itself matches the other case)
        var existing = File.Exists(targetPath) ? targetPath : null;
        if (existing != null)
        {
            // Both versions exist (interrupted conversion or manual copy): keep one only when they are the same
            if (!SameContent(existing, plaintext, contentKind, keys, out var why))
            {
                Shell.Logger.Warn("Storage: {file} and {other} differ ({why}), both kept", name,
                    Path.GetFileName(existing), why);
                result.Skipped.Add(new SkippedFile(name, path, SkippedFileReason.Conflict, toEncrypted,
                    $"Differs from {Path.GetFileName(existing)}: {why}"));
                return;
            }

            File.Delete(path);
            StorageFaults.Point(StorageFaults.CONVERT_SOURCE_DELETED, path);
            result.Deduplicated++;
            return;
        }

        if (toEncrypted)
        {
            var encrypted = StorageCrypto.Encrypt(plaintext, keys.MafileKey, keys.StorageId, contentKind);
            AtomicFile.Write(targetPath, encrypted, false, EncryptedValidator(plaintext, contentKind, keys));
        }
        else
        {
            AtomicFile.Write(targetPath, plaintext, false);
        }

        StorageFaults.Point(StorageFaults.CONVERT_PUBLISHED, targetPath);
        File.Delete(path);
        StorageFaults.Point(StorageFaults.CONVERT_SOURCE_DELETED, path);
        result.Converted++;
    }

    /// <summary>
    ///     Validates a plain file and removes the legacy password field. Null (and <paramref name="problem" />) when the
    ///     file is not accepted.
    /// </summary>
    private byte[]? PreparePlain(string dir, string name, StorageFileKind kind, byte[] source,
        out SkippedFile? problem)
    {
        problem = null;
        var path = Path.Combine(dir, name);
        if (kind == StorageFileKind.PlainZip)
        {
            try
            {
                return StorageContent.StripPasswordsInZip(source, out _);
            }
            catch (FormatException ex)
            {
                problem = new SkippedFile(name, path, SkippedFileReason.Invalid, true, ex.Message);
                return null;
            }
        }

        byte[] stripped;
        try
        {
            stripped = StorageContent.StripPassword(source, out _);
        }
        catch (FormatException ex)
        {
            problem = new SkippedFile(name, path, SkippedFileReason.Invalid, true, ex.Message);
            return null;
        }

        // Active accounts must be valid mafiles; removed / backup folders keep any JSON (backups of linking are raw
        // Steam responses)
        if (dir == Layout.MafilesDirectory)
        {
            var reason = ValidateMafile(stripped, name, out var details);
            if (reason != null)
            {
                problem = new SkippedFile(name, path, reason.Value, true, details);
                return null;
            }
        }

        return stripped;
    }

    private static SkippedFileReason? ValidateMafile(byte[] json, string name, out string? details)
    {
        details = null;
        try
        {
            NebulaSerializer.Deserialize(Encoding.UTF8.GetString(json), name);
            return null;
        }
        catch (MafileNeedReloginException)
        {
            // Complete secrets with an invalid SteamID: still protected (encrypted); reported when loading
            return null;
        }
        catch (Exception ex)
        {
            details = ex.Message;
            return SkippedFileReason.Invalid;
        }
    }

    private bool SameContent(string existingPath, byte[] plaintext, EncryptedContentKind contentKind, StorageKeys keys,
        out string why)
    {
        why = string.Empty;
        byte[] existing;
        try
        {
            existing = File.ReadAllBytes(existingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = "can't be read: " + ex.Message;
            return false;
        }

        var existingIsEncrypted = StorageFileNames.IsEncrypted(StorageFileNames.Classify(Path.GetFileName(existingPath)));
        if (existingIsEncrypted)
        {
            try
            {
                existing = StorageCrypto.Decrypt(existing, keys.MafileKey, keys.StorageId, contentKind);
            }
            catch (EnvelopeException ex)
            {
                why = "not readable with this storage key: " + ex.Error;
                return false;
            }
        }
        else if (StorageCrypto.HasMagic(existing))
        {
            why = "encrypted content in a plain file";
            return false;
        }

        var same = contentKind == EncryptedContentKind.Zip
            ? StorageContent.ZipsEquivalent(existing, plaintext)
            : StorageContent.MafilesEquivalent(existing, plaintext);
        if (!same) why = "different content";
        return same;
    }

    /// <summary>
    ///     A file already in the target format of the conversion: header check only (full check happens on load).
    /// </summary>
    private SkippedFile? CheckTargetFormatFile(string path, StorageFileKind kind, StorageKeys keys)
    {
        var name = Path.GetFileName(path);
        byte[] head;
        try
        {
            head = ReadHead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SkippedFile(name, path, SkippedFileReason.Unreadable, StorageFileNames.IsPlain(kind),
                ex.Message);
        }

        if (StorageFileNames.IsPlain(kind))
        {
            return StorageCrypto.HasMagic(head)
                ? new SkippedFile(name, path, SkippedFileReason.ExtensionMismatch, false,
                    "Encrypted content in a file with a plain extension")
                : null;
        }

        var error = StorageCrypto.CheckHeader(head, keys.StorageId, StorageFileNames.ContentKind(kind));
        if (error == EnvelopeError.None) return null;
        return new SkippedFile(name, path, MapEnvelopeError(error), false, error.ToString());
    }

    private static byte[] ReadHead(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[Math.Min(fs.Length, StorageCrypto.MIN_FILE_SIZE)];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private static AtomicWriteValidator EncryptedValidator(byte[] plaintext, EncryptedContentKind kind,
        StorageKeys keys)
    {
        return written =>
        {
            try
            {
                var check = StorageCrypto.Decrypt(written, keys.MafileKey, keys.StorageId, kind);
                return check.AsSpan().SequenceEqual(plaintext) ? null : "decrypted content differs";
            }
            catch (EnvelopeException ex)
            {
                return ex.Error.ToString();
            }
        };
    }

    public static SkippedFileReason MapEnvelopeError(EnvelopeError error)
    {
        return error switch
        {
            EnvelopeError.NotNebulaFormat => SkippedFileReason.UnsupportedFormat,
            EnvelopeError.UnsupportedVersion => SkippedFileReason.UnsupportedFormat,
            EnvelopeError.KindMismatch => SkippedFileReason.ExtensionMismatch,
            EnvelopeError.ForeignStorage => SkippedFileReason.ForeignStorage,
            _ => SkippedFileReason.Corrupted
        };
    }

    #endregion

    #region Upgrade / plain scan

    /// <summary>
    ///     First 2.x start: removes the legacy "Password" field (encrypted 1.x Steam password, never decrypted) from every
    ///     readable mafile of all storage folders and from mafiles inside backup ZIPs. Idempotent: files without the
    ///     field are not touched, so an interrupted run is simply repeated.
    /// </summary>
    /// <returns>Files that could not be read (retried on the next start: <see cref="StorageState.PendingLegacyStrip" />).</returns>
    private int UpgradeLegacy(StorageOperationResult result, IProgress<StorageProgress>? progress,
        CancellationToken token)
    {
        var unreadable = 0;
        var files = EnumerateStorageFiles().ToList();
        result.Total += files.Count;
        var processed = 0;
        foreach (var (_, path, kind) in files)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new StorageProgress(processed++, files.Count, Path.GetFileName(path)));
            var name = Path.GetFileName(path);
            if (StorageFileNames.IsEncrypted(kind))
            {
                result.Skipped.Add(new SkippedFile(name, path, SkippedFileReason.EncryptedInPlainStorage, false));
                continue;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                if (StorageCrypto.HasMagic(bytes))
                {
                    result.Skipped.Add(new SkippedFile(name, path, SkippedFileReason.ExtensionMismatch, false,
                        "Encrypted content in a file with a plain extension"));
                    continue;
                }

                bool changed;
                byte[] stripped;
                try
                {
                    stripped = kind == StorageFileKind.PlainZip
                        ? StorageContent.StripPasswordsInZip(bytes, out changed)
                        : StorageContent.StripPassword(bytes, out changed);
                }
                catch (FormatException ex)
                {
                    result.Skipped.Add(new SkippedFile(name, path, SkippedFileReason.Invalid, false, ex.Message));
                    continue;
                }

                if (!changed) continue;
                AtomicFile.Write(path, stripped, true, kind == StorageFileKind.PlainZip
                    ? written => StorageContent.TryReadZip(written) != null ? null : "not a readable ZIP"
                    : written => StorageContent.TryParseObject(written) is { } j && !StorageContent.HasLegacyPassword(j)
                        ? null
                        : "not a JSON object without the password field");
                StorageFaults.Point(StorageFaults.UPGRADE_FILE_DONE, path);
                result.Converted++;
            }
            catch (Exception ex) when (!StorageFaults.IsSimulatedCrash(ex) &&
                                       ex is IOException or UnauthorizedAccessException)
            {
                Shell.Logger.Error(ex, "Storage upgrade: can't remove the legacy password from {file}", name);
                result.Skipped.Add(new SkippedFile(name, path, SkippedFileReason.Unreadable, false, ex.Message));
                unreadable++;
            }
        }

        return unreadable;
    }

    /// <summary>
    ///     Plain storage: encrypted files can't be read (no key) and are only reported.
    /// </summary>
    private void ScanPlain(StorageOperationResult result)
    {
        foreach (var (_, path, kind) in EnumerateStorageFiles())
        {
            result.Total++;
            if (StorageFileNames.IsEncrypted(kind))
            {
                result.Skipped.Add(new SkippedFile(Path.GetFileName(path), path,
                    SkippedFileReason.EncryptedInPlainStorage, false));
            }
        }
    }

    /// <summary>
    ///     Temporary files of <see cref="AtomicFile" /> left by a crash: removed when the target file (or its
    ///     other-format counterpart) is intact, otherwise kept and reported (it may hold the only copy of new data).
    /// </summary>
    private void CleanupTemps(StorageOperationResult result)
    {
        foreach (var dir in Layout.AccountDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var temp in Directory.GetFiles(dir))
            {
                var tempName = Path.GetFileName(temp);
                var target = AtomicFile.GetTempTarget(tempName);
                if (target == null) continue;
                var targetKind = StorageFileNames.Classify(target);
                var counterpart = StorageFileNames.ToName(target, !StorageFileNames.IsEncrypted(targetKind));
                if (IsIntact(Path.Combine(dir, target)) || IsIntact(Path.Combine(dir, counterpart)))
                {
                    AtomicFile.TryDelete(temp);
                    continue;
                }

                bool plain;
                try
                {
                    plain = !StorageCrypto.HasMagic(ReadHead(temp));
                }
                catch (Exception)
                {
                    plain = false;
                }

                Shell.Logger.Warn("Storage: unfinished write {temp} kept, its target is missing or damaged", tempName);
                result.Skipped.Add(new SkippedFile(tempName, temp, SkippedFileReason.UnfinishedWrite,
                    plain && Mode != StorageMode.Plain, $"Temporary file of {target}"));
            }
        }

        // Temporary files of the state / password store: the operation that created them failed before publishing,
        // nothing depends on them
        if (!Directory.Exists(Layout.Root)) return;
        var stateName = Path.GetFileName(Layout.StateFile);
        var passwordsName = Path.GetFileName(Layout.PasswordsFile);
        var previousName = Path.GetFileName(Layout.PreviousStateFile);
        foreach (var temp in Directory.GetFiles(Layout.Root))
        {
            var target = AtomicFile.GetTempTarget(Path.GetFileName(temp));
            if (target != null && (target.Equals(stateName, StringComparison.OrdinalIgnoreCase) ||
                                   target.Equals(passwordsName, StringComparison.OrdinalIgnoreCase) ||
                                   target.Equals(previousName, StringComparison.OrdinalIgnoreCase)))
            {
                AtomicFile.TryDelete(temp);
            }
        }
    }

    private bool IsIntact(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var kind = StorageFileNames.Classify(Path.GetFileName(path));
            var bytes = File.ReadAllBytes(path);
            if (StorageFileNames.IsEncrypted(kind))
            {
                if (_keys == null) return StorageCrypto.HasMagic(bytes) && bytes.Length >= StorageCrypto.MIN_FILE_SIZE;
                StorageCrypto.Decrypt(bytes, _keys.MafileKey, _keys.StorageId, StorageFileNames.ContentKind(kind));
                return true;
            }

            return kind == StorageFileKind.PlainZip
                ? StorageContent.TryReadZip(bytes) != null
                : StorageContent.TryParseObject(bytes) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    #endregion

    #region Account files (used by Storage)

    private bool WriteEncrypted => Mode is StorageMode.Encrypted or StorageMode.Encrypting;
    private bool Transitional => Mode is StorageMode.Encrypting or StorageMode.Decrypting;

    /// <summary>
    ///     Mafiles of <paramref name="dir" /> to load in the current mode: plain storage — plain files, encrypted —
    ///     encrypted files, during a mode change — both (one per logical name, the target format wins). Files of the
    ///     other format were handled (converted or reported) by <see cref="PrepareAsync" />.
    /// </summary>
    public IReadOnlyList<AccountFile> EnumerateLoadableMafiles(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var result = new Dictionary<string, AccountFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(path);
            var kind = StorageFileNames.Classify(name);
            if (!StorageFileNames.IsMafile(kind)) continue;
            var encrypted = kind == StorageFileKind.EncryptedMafile;
            if (!Transitional && encrypted != WriteEncrypted) continue;
            var file = new AccountFile(path, StorageFileNames.ToPlainName(name), encrypted);
            if (result.TryGetValue(file.LogicalName, out var other))
            {
                if (other.Encrypted == encrypted)
                {
                    // Linux: names that differ only in case are different files
                    result[file.LogicalName + "|" + path] = file;
                    continue;
                }

                if (other.Encrypted == WriteEncrypted) continue;
            }

            result[file.LogicalName] = file;
        }

        return result.Values.ToList();
    }

    /// <summary>
    ///     Reads a storage file and returns its plain content (decrypted when needed). The format is taken from the
    ///     header; the extension must agree with it.
    /// </summary>
    /// <exception cref="StorageFileException">The file can't be used in the current mode.</exception>
    public byte[] ReadFile(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageFileException(SkippedFileReason.Unreadable, ex.Message, ex);
        }

        return Decode(path, bytes);
    }

    /// <inheritdoc cref="ReadFile" />
    public async Task<byte[]> ReadFileAsync(string path, CancellationToken token = default)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageFileException(SkippedFileReason.Unreadable, ex.Message, ex);
        }

        return Decode(path, bytes);
    }

    private byte[] Decode(string path, byte[] bytes)
    {
        var kind = StorageFileNames.Classify(Path.GetFileName(path));
        var encryptedName = StorageFileNames.IsEncrypted(kind);
        if (!StorageCrypto.HasMagic(bytes))
        {
            if (encryptedName)
                throw new StorageFileException(SkippedFileReason.UnsupportedFormat,
                    "Not a NebulaAuth encrypted file");
            return bytes;
        }

        if (!encryptedName)
            throw new StorageFileException(SkippedFileReason.ExtensionMismatch,
                "Encrypted content in a file with a plain extension");
        var keys = _keys ?? throw new StorageFileException(SkippedFileReason.EncryptedInPlainStorage,
            "The storage is not encrypted or is locked");
        try
        {
            return StorageCrypto.Decrypt(bytes, keys.MafileKey, keys.StorageId, StorageFileNames.ContentKind(kind));
        }
        catch (EnvelopeException ex)
        {
            throw new StorageFileException(MapEnvelopeError(ex.Error), ex.Message, ex);
        }
    }

    /// <summary>
    ///     Plain content of a mafile to import from outside the storage folders (file picker, drag and drop, clipboard).
    ///     Plain content is returned as is. An encrypted NebulaAuth file (decided by the header, not by the name) is
    ///     decrypted only when it belongs to this storage; it is never re-encrypted or read with another key.
    /// </summary>
    /// <exception cref="StorageFileException">
    ///     Encrypted file of another storage / format, damaged, or the storage has no key (plain mode).
    /// </exception>
    public byte[] DecodeImportedFile(byte[] bytes)
    {
        if (!StorageCrypto.HasMagic(bytes)) return bytes;
        var keys = _keys ?? throw new StorageFileException(SkippedFileReason.EncryptedInPlainStorage,
            "The storage is not encrypted or is locked");
        try
        {
            return StorageCrypto.Decrypt(bytes, keys.MafileKey, keys.StorageId, EncryptedContentKind.Mafile);
        }
        catch (EnvelopeException ex)
        {
            throw new StorageFileException(MapEnvelopeError(ex.Error), ex.Message, ex);
        }
    }

    /// <summary>
    ///     Existing physical file of a mafile (target format first), or null.
    /// </summary>
    public string? FindAccountFile(string dir, string logicalName)
    {
        return StorageFileNames.FindFile(dir, StorageFileNames.ToName(logicalName, WriteEncrypted)) ??
               StorageFileNames.FindFile(dir, StorageFileNames.ToName(logicalName, !WriteEncrypted));
    }

    /// <summary>
    ///     Physical path of a mafile: the existing file, or the path a new file gets in the current mode.
    /// </summary>
    public string GetAccountFilePath(string dir, string logicalName)
    {
        return FindAccountFile(dir, logicalName) ??
               Path.Combine(dir, StorageFileNames.ToName(logicalName, WriteEncrypted));
    }

    public bool AccountFileExists(string dir, string logicalName)
    {
        return FindAccountFile(dir, logicalName) != null;
    }

    /// <summary>
    ///     Writes a mafile / backup (plain content) in the format of the current mode through <see cref="AtomicFile" />.
    ///     During a mode change the file of the other format is removed after the new one is published.
    /// </summary>
    public async Task WriteAccountFileAsync(string dir, string logicalName, byte[] plaintext,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (path, data, validator) = PrepareWrite(dir, logicalName, plaintext, EncryptedContentKind.Mafile);
            await AtomicFile.WriteAsync(path, data, true, validator, token).ConfigureAwait(false);
            AfterWrite(dir, logicalName);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc cref="WriteAccountFileAsync" />
    public void WriteAccountFile(string dir, string logicalName, byte[] plaintext)
    {
        _gate.Wait();
        try
        {
            var (path, data, validator) = PrepareWrite(dir, logicalName, plaintext, EncryptedContentKind.Mafile);
            AtomicFile.Write(path, data, true, validator);
            AfterWrite(dir, logicalName);
        }
        finally
        {
            _gate.Release();
        }
    }

    private (string path, byte[] data, AtomicWriteValidator? validator) PrepareWrite(string dir, string logicalName,
        byte[] plaintext, EncryptedContentKind kind)
    {
        var encrypted = WriteEncrypted;
        var name = StorageFileNames.ToName(logicalName, encrypted);
        var path = StorageFileNames.FindFile(dir, name) ?? Path.Combine(dir, name);
        if (!encrypted) return (path, plaintext, null);
        var keys = RequireKeys();
        var data = StorageCrypto.Encrypt(plaintext, keys.MafileKey, keys.StorageId, kind);
        return (path, data, EncryptedValidator(plaintext, kind, keys));
    }

    private void AfterWrite(string dir, string logicalName)
    {
        if (!Transitional) return;
        var other = StorageFileNames.FindFile(dir, StorageFileNames.ToName(logicalName, !WriteEncrypted));
        if (other != null) File.Delete(other);
    }

    /// <summary>
    ///     Moves a mafile (every format present) to <paramref name="destDir" /> under a free name
    ///     ("name (1).mafile"...). Returns the new physical path of the main file.
    /// </summary>
    public string MoveAccountFile(string sourceDir, string logicalName, string destDir)
    {
        _gate.Wait();
        try
        {
            var main = FindAccountFile(sourceDir, logicalName) ??
                       throw new FileNotFoundException("The mafile does not exist", logicalName);
            var sources = new List<string> {main};
            var other = StorageFileNames.FindFile(sourceDir, StorageFileNames.ToName(logicalName,
                !StorageFileNames.IsEncrypted(StorageFileNames.Classify(Path.GetFileName(main)))));
            if (other != null && !string.Equals(other, main, StringComparison.OrdinalIgnoreCase)) sources.Add(other);

            Directory.CreateDirectory(destDir);
            var baseName = Path.GetFileNameWithoutExtension(logicalName);
            var ext = Path.GetExtension(logicalName);
            if (string.IsNullOrEmpty(ext)) ext = StorageFileNames.MAFILE_EXTENSION;
            var candidate = baseName + ext;
            for (var i = 1; FindFile(candidate, false) || FindFile(candidate, true); i++)
            {
                candidate = $"{baseName} ({i}){ext}";
            }

            string? result = null;
            foreach (var source in sources)
            {
                var encrypted = StorageFileNames.IsEncrypted(StorageFileNames.Classify(Path.GetFileName(source)));
                var dest = Path.Combine(destDir, StorageFileNames.ToName(candidate, encrypted));
                File.Move(source, dest, false);
                result ??= dest;
            }

            return result!;

            bool FindFile(string plainName, bool encrypted)
            {
                return StorageFileNames.FindFile(destDir, StorageFileNames.ToName(plainName, encrypted)) != null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Renames a mafile in <paramref name="dir" /> (logical names; the physical extension is kept).
    /// </summary>
    public RenameAccountFileResult RenameAccountFile(string dir, string oldLogicalName, string newLogicalName)
    {
        _gate.Wait();
        try
        {
            var source = FindAccountFile(dir, oldLogicalName);
            if (source == null) return RenameAccountFileResult.SourceMissing;
            if (AccountFileExists(dir, newLogicalName)) return RenameAccountFileResult.TargetExists;
            var encrypted = StorageFileNames.IsEncrypted(StorageFileNames.Classify(Path.GetFileName(source)));
            File.Move(source, Path.Combine(dir, StorageFileNames.ToName(newLogicalName, encrypted)), false);
            return RenameAccountFileResult.Renamed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Publishes a backup ZIP (plain ZIP bytes) in the backup folder as a new file: <c>name.zip</c> or
    ///     <c>name.zip.encrypted</c>. Returns the published file name.
    /// </summary>
    public async Task<string> WriteBackupArchiveAsync(string zipFileName, byte[] zipBytes,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var encrypted = WriteEncrypted;
            var name = StorageFileNames.ToName(zipFileName, encrypted);
            var path = Path.Combine(Layout.BackupDirectory, name);
            if (!encrypted)
            {
                await AtomicFile.WriteAsync(path, zipBytes, false,
                    w => StorageContent.TryReadZip(w) != null ? null : "not a readable ZIP", token).ConfigureAwait(false);
                return name;
            }

            var keys = RequireKeys();
            var data = StorageCrypto.Encrypt(zipBytes, keys.MafileKey, keys.StorageId, EncryptedContentKind.Zip);
            await AtomicFile.WriteAsync(path, data, false, EncryptedValidator(zipBytes, EncryptedContentKind.Zip, keys),
                token).ConfigureAwait(false);
            return name;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Removes the legacy password field from a plain mafile found while loading (manually added in plain mode).
    /// </summary>
    public void StripLegacyPassword(string path)
    {
        _gate.Wait();
        try
        {
            if (StorageFileNames.Classify(Path.GetFileName(path)) != StorageFileKind.PlainMafile) return;
            var bytes = File.ReadAllBytes(path);
            if (StorageCrypto.HasMagic(bytes)) return;
            var stripped = StorageContent.StripPassword(bytes, out var changed);
            if (!changed) return;
            AtomicFile.Write(path, stripped, true,
                w => StorageContent.TryParseObject(w) is { } j && !StorageContent.HasLegacyPassword(j)
                    ? null
                    : "not a JSON object without the password field");
        }
        finally
        {
            _gate.Release();
        }
    }

    #endregion

    #region Skipped files

    public void ReportSkipped(SkippedFile file)
    {
        ReportSkipped([file]);
    }

    public void ReportSkipped(IEnumerable<SkippedFile> files)
    {
        var added = false;
        lock (_skippedLock)
        {
            var known = new HashSet<string>(_skipped.Select(s => s.Path), StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (!known.Add(file.Path)) continue;
                _skipped.Add(file);
                added = true;
                Shell.Logger.Warn("Storage: skipped {file} ({reason}{unprotected}) {details}", file.Name, file.Reason,
                    file.IsUnprotectedPlain ? ", not protected" : string.Empty, file.Details ?? string.Empty);
            }
        }

        if (added) SkippedFilesChanged?.Invoke();
    }

    /// <summary>
    ///     After an operation in the running app: its result replaces the entries of the files it examined; earlier
    ///     entries of other files that still exist (unfinished writes, files skipped while loading...) are kept. The
    ///     "not protected" flag only applies while the storage is not plain.
    /// </summary>
    private void MergeSkipped(StorageOperationResult result)
    {
        var plain = Mode == StorageMode.Plain;
        var fresh = result.AllSkipped.Select(f => plain && f.IsUnprotectedPlain ? f with {IsUnprotectedPlain = false} : f)
            .ToList();
        var paths = new HashSet<string>(fresh.Select(f => f.Path), StringComparer.Ordinal);
        List<SkippedFile> kept;
        lock (_skippedLock)
        {
            kept = _skipped.Where(old => !paths.Contains(old.Path) && File.Exists(old.Path) &&
                                         old.Reason is not (SkippedFileReason.EncryptedInPlainStorage
                                             or SkippedFileReason.ConversionFailed))
                .Select(old => plain && old.IsUnprotectedPlain ? old with {IsUnprotectedPlain = false} : old)
                .ToList();
            _skipped = kept;
        }

        ReportSkipped(fresh);
        SkippedFilesChanged?.Invoke();
    }

    private static void LogResult(string operation, StorageOperationResult result)
    {
        Shell.Logger.Info(
            "{operation}: mode {mode}, completed {completed}, files {total}, converted {converted}, deduplicated {dedup}, skipped {skipped}, failed {failed}",
            operation, result.ModeAfter, result.Completed, result.Total, result.Converted, result.Deduplicated,
            result.Skipped.Count, result.Failed.Count);
    }

    #endregion

    private sealed class StorageKeys(byte[] storageId, byte[] mafileKey, byte[] passwordsKey, byte[] stateMacKey)
        : IDisposable
    {
        public byte[] StorageId { get; } = storageId;
        public byte[] MafileKey { get; } = mafileKey;
        public byte[] PasswordsKey { get; } = passwordsKey;
        public byte[] StateMacKey { get; } = stateMacKey;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(MafileKey);
            CryptographicOperations.ZeroMemory(PasswordsKey);
            CryptographicOperations.ZeroMemory(StateMacKey);
        }
    }
}
