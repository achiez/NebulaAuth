using System.Collections.Generic;
using System.IO;
using NebulaAuth.Core;

namespace NebulaAuth.Model.Mafiles.Protection;

/// <summary>
///     Absolute paths of the storage in the data folder.
/// </summary>
public sealed record StorageLayout(
    string Root,
    string MafilesDirectory,
    string RemovedDirectory,
    string BackupDirectory,
    string StateFile,
    string PasswordsFile)
{
    public const string DIR_MAFILES = "maFiles";
    public const string DIR_REMOVED_MAFILES = "maFiles_removed";
    public const string DIR_BACKUP_MAFILES = "maFiles_backup";

    /// <summary>
    ///     maFiles, maFiles_removed, maFiles_backup.
    /// </summary>
    public IReadOnlyList<string> AccountDirectories => [MafilesDirectory, RemovedDirectory, BackupDirectory];

    /// <summary>
    ///     storage-state.json.prev: the last state with a key, kept when encryption is disabled.
    /// </summary>
    public string PreviousStateFile => StateFile + StorageState.PREVIOUS_SUFFIX;

    /// <summary>
    ///     Layout of the resolved data folder (<see cref="DataRoot" />).
    /// </summary>
    public static StorageLayout FromDataRoot()
    {
        return new StorageLayout(DataRoot.Path,
            DataRoot.Combine(DIR_MAFILES),
            DataRoot.Combine(DIR_REMOVED_MAFILES),
            DataRoot.Combine(DIR_BACKUP_MAFILES),
            DataRoot.Combine(StorageState.FILE_NAME),
            DataRoot.Combine(SteamPasswordStore.FILE_NAME));
    }

    /// <summary>
    ///     Layout of any folder (tests, tools). Existing entries that differ only in case are reused.
    /// </summary>
    public static StorageLayout ForFolder(string root)
    {
        root = Path.GetFullPath(root);
        return new StorageLayout(root,
            Child(DIR_MAFILES),
            Child(DIR_REMOVED_MAFILES),
            Child(DIR_BACKUP_MAFILES),
            Child(StorageState.FILE_NAME),
            Child(SteamPasswordStore.FILE_NAME));

        string Child(string name)
        {
            var exact = Path.Combine(root, name);
            if (File.Exists(exact) || Directory.Exists(exact) || !Directory.Exists(root)) return exact;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (string.Equals(Path.GetFileName(entry), name, System.StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            return exact;
        }
    }
}
