using System;
using System.IO;
using System.Linq;

namespace NebulaAuth.Model.Mafiles.Protection;

public enum StorageFileKind
{
    Other,
    Temp,
    PlainMafile,
    EncryptedMafile,
    PlainZip,
    EncryptedZip
}

/// <summary>
///     File names of the storage. A mafile has a logical name (<c>76561198000000000.mafile</c>, the value of
///     <see cref="Entities.Mafile.Filename" />, independent of the mode) and a physical name: the same in plain mode,
///     <c>&lt;logical&gt;.encrypted</c> in encrypted mode. Backup ZIPs: <c>*.zip</c> / <c>*.zip.encrypted</c>.
///     Extensions are compared ignoring case (SDA writes "*.maFile"); the extension only helps the user, the format is
///     decided by the file header.
/// </summary>
public static class StorageFileNames
{
    public const string MAFILE_EXTENSION = ".mafile";
    public const string ZIP_EXTENSION = ".zip";
    public const string ENCRYPTED_SUFFIX = ".encrypted";

    public static StorageFileKind Classify(string fileName)
    {
        if (AtomicFile.IsTempFile(fileName)) return StorageFileKind.Temp;
        if (fileName.EndsWith(MAFILE_EXTENSION + ENCRYPTED_SUFFIX, StringComparison.OrdinalIgnoreCase))
            return StorageFileKind.EncryptedMafile;
        if (fileName.EndsWith(MAFILE_EXTENSION, StringComparison.OrdinalIgnoreCase))
            return StorageFileKind.PlainMafile;
        if (fileName.EndsWith(ZIP_EXTENSION + ENCRYPTED_SUFFIX, StringComparison.OrdinalIgnoreCase))
            return StorageFileKind.EncryptedZip;
        if (fileName.EndsWith(ZIP_EXTENSION, StringComparison.OrdinalIgnoreCase))
            return StorageFileKind.PlainZip;
        return StorageFileKind.Other;
    }

    public static bool IsEncrypted(StorageFileKind kind)
    {
        return kind is StorageFileKind.EncryptedMafile or StorageFileKind.EncryptedZip;
    }

    public static bool IsPlain(StorageFileKind kind)
    {
        return kind is StorageFileKind.PlainMafile or StorageFileKind.PlainZip;
    }

    public static bool IsMafile(StorageFileKind kind)
    {
        return kind is StorageFileKind.PlainMafile or StorageFileKind.EncryptedMafile;
    }

    public static EncryptedContentKind ContentKind(StorageFileKind kind)
    {
        return kind is StorageFileKind.PlainZip or StorageFileKind.EncryptedZip
            ? EncryptedContentKind.Zip
            : EncryptedContentKind.Mafile;
    }

    /// <summary>
    ///     Plain name: removes the ".encrypted" suffix (any case) when present.
    /// </summary>
    public static string ToPlainName(string fileName)
    {
        return fileName.EndsWith(ENCRYPTED_SUFFIX, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ENCRYPTED_SUFFIX.Length]
            : fileName;
    }

    public static string ToEncryptedName(string fileName)
    {
        return fileName.EndsWith(ENCRYPTED_SUFFIX, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ENCRYPTED_SUFFIX;
    }

    public static string ToName(string plainName, bool encrypted)
    {
        return encrypted ? ToEncryptedName(plainName) : ToPlainName(plainName);
    }

    /// <summary>
    ///     Existing entry of <paramref name="folder" /> named <paramref name="fileName" />: the exact name wins, otherwise a
    ///     name that differs only in case (case-sensitive file systems: Linux). Null when there is none.
    /// </summary>
    public static string? FindFile(string folder, string fileName)
    {
        var exact = Path.Combine(folder, fileName);
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
    }
}
