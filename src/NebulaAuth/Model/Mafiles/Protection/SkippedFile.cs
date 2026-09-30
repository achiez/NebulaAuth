namespace NebulaAuth.Model.Mafiles.Protection;

public enum SkippedFileReason
{
    /// <summary>Not a valid mafile / backup (not JSON, not a readable ZIP, missing data).</summary>
    Invalid,

    /// <summary>The mafile is valid but has no valid SteamID (needs a new login through import).</summary>
    NeedsRelogin,

    /// <summary>The file can't be read (access denied, I/O error).</summary>
    Unreadable,

    /// <summary>Encrypted by another NebulaAuth storage (other key / master password).</summary>
    ForeignStorage,

    /// <summary>Encrypted file of an unknown format (another program) or of a newer NebulaAuth version.</summary>
    UnsupportedFormat,

    /// <summary>The file header does not match the file extension (e.g. a renamed encrypted file).</summary>
    ExtensionMismatch,

    /// <summary>Integrity check failed: the encrypted file is truncated or changed.</summary>
    Corrupted,

    /// <summary>An encrypted file in a plain (not encrypted) storage: there is no key to read it.</summary>
    EncryptedInPlainStorage,

    /// <summary>Plain and encrypted versions of the same file exist and differ: both are kept.</summary>
    Conflict,

    /// <summary>Converting the file to the storage mode failed (write / verification error); retried on next start.</summary>
    ConversionFailed,

    /// <summary>
    ///     An interrupted write left only a temporary file (the target is missing or damaged): kept for manual recovery.
    /// </summary>
    UnfinishedWrite
}

/// <summary>
///     A file in the storage folders that is not loaded / not converted to the current mode. It stays in place; the user
///     decides what to do with it (skipped-files screen).
/// </summary>
/// <param name="Name">File name.</param>
/// <param name="Path">Full path.</param>
/// <param name="Reason">Why it was skipped.</param>
/// <param name="IsUnprotectedPlain">
///     A plain (readable) file with secrets in an encrypted storage: not protected by the current mode.
/// </param>
/// <param name="Details">Technical details for the log / tooltip (no secrets).</param>
public sealed record SkippedFile(
    string Name,
    string Path,
    SkippedFileReason Reason,
    bool IsUnprotectedPlain,
    string? Details = null);
