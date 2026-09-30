using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Exceptions;
using NebulaAuth.Model.MAAC;
using NebulaAuth.Model.Mafiles.Protection;
using SteamLib;
using SteamLib.Exceptions.Authorization;
using SteamLib.SteamMobile;

namespace NebulaAuth.Model.Mafiles;

/// <summary>
///     Loaded accounts and mafile operations. Files are read / written only through the storage layer
///     (<see cref="MafileStorageService" />): it applies the storage mode (plain / encrypted) and publishes every file
///     atomically. <see cref="Mafile.Filename" /> is the logical name ("*.mafile"), also in the encrypted mode.
/// </summary>
public static class Storage
{
    public const string DIR_MAFILES = StorageLayout.DIR_MAFILES;
    public const string DIR_REMOVED_MAFILES = StorageLayout.DIR_REMOVED_MAFILES;
    public const string DIR_BACKUP_MAFILES = StorageLayout.DIR_BACKUP_MAFILES;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static MafileStorageService Service => MafileStorageService.Current;

    public static IReadOnlyList<string> MafilesDirectories => Service.Layout.AccountDirectories;

    // Absolute paths in the data folder; an existing folder that differs only in case is reused (Linux)
    public static string MafilesDirectory => Service.Layout.MafilesDirectory;
    public static string RemovedMafilesDirectory => Service.Layout.RemovedDirectory;
    public static string BackupMafilesDirectory => Service.Layout.BackupDirectory;

    public static ObservableCollection<Mafile> MaFiles { get; private set; } = [];

    /// <summary>
    ///     Loads the accounts. Must run after <see cref="MafileStorageService.PrepareAsync" /> (storage unlocked and
    ///     reconciled). Files that can't be loaded are reported to <see cref="MafileStorageService.SkippedFiles" />.
    /// </summary>
    public static async Task Initialize(int threadCount, CancellationToken token = default)
    {
        foreach (var dir in MafilesDirectories) Directory.CreateDirectory(dir);

        var service = Service;
        var files = service.EnumerateLoadableMafiles(MafilesDirectory);
        var localList = new ConcurrentBag<Mafile>();
        await Task.Run(() =>
        {
            return Parallel.ForEachAsync(files,
                new ParallelOptions {CancellationToken = token, MaxDegreeOfParallelism = threadCount},
                async (file, ct) =>
                {
                    try
                    {
                        var bytes = await service.ReadFileAsync(file.Path, ct);
                        var data = NebulaSerializer.Deserialize(Utf8NoBom.GetString(bytes), file.LogicalName,
                            out var hasLegacyPassword);
                        localList.Add(data);
                        if (hasLegacyPassword && !file.Encrypted)
                        {
                            // Plain mafile added manually with the old Steam password field
                            service.StripLegacyPassword(file.Path);
                        }
                    }
                    catch (StorageFileException ex)
                    {
                        service.ReportSkipped(new SkippedFile(Path.GetFileName(file.Path), file.Path, ex.Reason,
                            !file.Encrypted && service.Mode != StorageMode.Plain, ex.Message));
                    }
                    catch (MafileNeedReloginException)
                    {
                        service.ReportSkipped(new SkippedFile(Path.GetFileName(file.Path), file.Path,
                            SkippedFileReason.NeedsRelogin, !file.Encrypted && service.Mode != StorageMode.Plain,
                            "The mafile has no valid SteamID"));
                    }
                    catch (Exception ex)
                    {
                        Shell.Logger.Error(ex, "Can't load mafile {file}", Path.GetFileName(file.Path));
                        service.ReportSkipped(new SkippedFile(Path.GetFileName(file.Path), file.Path,
                            SkippedFileReason.Invalid, !file.Encrypted && service.Mode != StorageMode.Plain,
                            ex.Message));
                    }
                });
        }, token);

        MaFiles = new ObservableCollection<Mafile>(localList.OrderBy(m => m.AccountName));
        await Task.Run(() => ReportDamagedRemovedMafiles(service, token), token);
        Shell.Logger.Info("Loaded {count} mafile(s) of {total} ({mode} storage), skipped files: {skipped}",
            MaFiles.Count, files.Count, service.Mode, service.SkippedFiles.Count);
    }

    /// <summary>
    ///     Removed mafiles are not loaded, but one that can't be read or is damaged (integrity check failed, not JSON) is
    ///     listed in the skipped files like the files of the maFiles folder.
    /// </summary>
    private static void ReportDamagedRemovedMafiles(MafileStorageService service, CancellationToken token)
    {
        foreach (var file in service.EnumerateLoadableMafiles(RemovedMafilesDirectory))
        {
            token.ThrowIfCancellationRequested();
            SkippedFileReason reason;
            string details;
            try
            {
                var bytes = service.ReadFile(file.Path);
                var valid = StorageContent.TryParseObject(bytes) != null;
                if (file.Encrypted) CryptographicOperations.ZeroMemory(bytes);
                if (valid) continue;
                reason = SkippedFileReason.Invalid;
                details = "Not a JSON object";
            }
            catch (StorageFileException ex)
            {
                reason = ex.Reason;
                details = ex.Message;
            }

            service.ReportSkipped(new SkippedFile(Path.GetFileName(file.Path), file.Path, reason,
                !file.Encrypted && service.Mode != StorageMode.Plain, details));
        }
    }

    /// <summary>
    /// </summary>
    /// <param name="path"></param>
    /// <param name="overwrite"></param>
    /// <returns> Result of adding mafile</returns>
    /// <exception cref="SessionInvalidException"></exception>
    public static async Task<AddMafileResult> AddNewMafile(string path, bool overwrite)
    {
        Mafile data;

        try
        {
            data = await ReadMafileAsync(path);
        }
        catch (Exception ex) when (ex is not MafileNeedReloginException)
        {
            Shell.Logger.Warn(ex, "Can't load mafile");
            throw new FormatException("File data is not valid", ex);
        }

        await AddNewMafileInternal(data, overwrite);
        return AddMafileResult.Added;
    }

    /// <summary>
    /// </summary>
    /// <param name="data"></param>
    /// <param name="overwrite"></param>
    /// <returns> Result of adding mafile</returns>
    /// <exception cref="SessionInvalidException"></exception>
    public static Task<AddMafileResult> AddNewMafileFromData(Mafile data, bool overwrite)
    {
        return AddNewMafileInternal(data, overwrite);
    }

    private static async Task<AddMafileResult> AddNewMafileInternal(Mafile data, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (string.IsNullOrWhiteSpace(data.AccountName))
        {
            Shell.Logger.Warn("Mafile account name is empty");
            return AddMafileResult.Error;
        }

        try
        {
            _ = SteamGuardCodeGenerator.GenerateCode(data.SharedSecret);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Can't generate code for mafile {accountName}", data.AccountName);
            return AddMafileResult.Error;
        }

        data.Filename = null;

        if (!overwrite && Service.AccountFileExists(MafilesDirectory, MafilesStorageHelper.GetOrUpdateMafileName(data)))
        {
            return AddMafileResult.AlreadyExist;
        }

        await SaveMafileAsync(data);
        return AddMafileResult.Added;
    }

    /// <summary>
    ///     Reads a mafile of the storage or an external plain mafile (the format is detected by the file header).
    /// </summary>
    public static async Task<Mafile> ReadMafileAsync(string path)
    {
        var bytes = await Service.ReadFileAsync(path);
        return NebulaSerializer.Deserialize(Utf8NoBom.GetString(bytes), StorageFileNames.ToPlainName(path));
    }

    /// <summary>
    ///     Writes the mafile and adds it to <see cref="MaFiles" /> (replaces the entry with the same file) after the
    ///     write succeeded.
    /// </summary>
    public static async Task SaveMafileAsync(Mafile data)
    {
        var name = MafilesStorageHelper.GetOrUpdateMafileName(data);
        await Service.WriteAccountFileAsync(MafilesDirectory, name, Serialize(data));

        // Replace if exist
        for (var i = 0; i < MaFiles.Count; i++)
        {
            var existed = MaFiles[i];
            if (ReferenceEquals(existed, data) ||
                (existed.Filename != null && existed.Filename.Equals(data.Filename, StringComparison.OrdinalIgnoreCase)))
            {
                MaFiles[i] = data;
                return;
            }
        }

        MaFiles.Add(data);
    }

    public static void UpdateMafile(Mafile data)
    {
        var name = MafilesStorageHelper.GetOrUpdateMafileName(data);
        Service.WriteAccountFile(MafilesDirectory, name, Serialize(data));
    }

    public static async Task UpdateMafileAsync(Mafile data)
    {
        var name = MafilesStorageHelper.GetOrUpdateMafileName(data);
        await Service.WriteAccountFileAsync(MafilesDirectory, name, Serialize(data));
    }

    private static byte[] Serialize(Mafile data)
    {
        return Utf8NoBom.GetBytes(NebulaSerializer.SerializeMafile(data));
    }

    public static void MoveToRemoved(Mafile data)
    {
        var name = MafilesStorageHelper.GetOrUpdateMafileName(data);
        Service.MoveAccountFile(MafilesDirectory, name, RemovedMafilesDirectory);
        MaFiles.Remove(data);
    }


    /// <summary>
    ///     Physical path of the mafile in the storage (encrypted in the encrypted mode).
    /// </summary>
    public static string? TryGetMafilePath(Mafile data)
    {
        if (data.Filename == null) return null;
        return Service.GetAccountFilePath(MafilesDirectory, data.Filename);
    }

    /// <summary>
    ///     Backup of a newly linked authenticator; <paramref name="deviceName" /> is the device name chosen for it, so a
    ///     mafile restored from the backup keeps it.
    /// </summary>
    public static void WriteBackup(MobileDataExtended data, string? deviceName = null)
    {
        var props = string.IsNullOrWhiteSpace(deviceName)
            ? null
            : new Dictionary<string, object?> {{NebulaSerializer.DEVICE_NAME_PROPERTY, deviceName}};
        var json = NebulaSerializer.SerializeMafile(data, props);
        WriteBackup(data.AccountName, json);
    }

    public static void WriteBackup(string accountName, string data)
    {
        Directory.CreateDirectory(BackupMafilesDirectory);
        Service.WriteAccountFile(BackupMafilesDirectory, accountName + MafileNamingStrategy.DEF_EXTENSION,
            Utf8NoBom.GetBytes(data));
    }

    public static async Task<MafilesBulkRenameResult> RenameMafiles(bool loginAsFileName,
        IProgress<double>? progress = null)
    {
        if (MaFiles.Count == 0) return new MafilesBulkRenameResult();
        var now = DateTime.Now;
        var backupFileName = $"rename_backup_{now:yyyy-MM-dd_HH-mm-ss}.zip";

        // The archive holds plain mafiles; the storage publishes it encrypted in the encrypted mode
        var service = Service;
        var files = service.EnumerateLoadableMafiles(MafilesDirectory);
        var items = new List<ZipItem>(files.Count);
        // Only files that are in the backup archive are renamed (a file that could not be read has no backup)
        var backedUp = new HashSet<string>(StringComparer.Ordinal);
        var counter = 0;
        foreach (var file in files)
        {
            counter++;
            try
            {
                var content = await service.ReadFileAsync(file.Path);
                items.Add(new ZipItem(file.LogicalName, File.GetLastWriteTime(file.Path), content));
                backedUp.Add(file.LogicalName);
            }
            catch (StorageFileException ex)
            {
                Shell.Logger.Warn("Rename backup: {file} skipped ({reason})", Path.GetFileName(file.Path), ex.Reason);
            }

            if (counter % 5 == 0)
            {
                progress?.Report(counter / (double) files.Count);
                await Task.Delay(10);
            }
        }

        var publishedName = await service.WriteBackupArchiveAsync(backupFileName,
            await Task.Run(() => StorageContent.CreateZip(items)));

        var mafiles = MaFiles.ToList();
        var res = new MafilesBulkRenameResult
        {
            Total = mafiles.Count,
            BackupFileName = publishedName
        };

        var dic = new Dictionary<string, string>();
        foreach (var mafile in mafiles)
        {
            try
            {
                var targetFileName = MafilesStorageHelper.CreateMafileFileName(mafile, loginAsFileName);
                if (mafile.Filename == targetFileName || mafile.Filename == null)
                {
                    res.Renamed += 1;
                    continue;
                }

                var sourceFileName = mafile.Filename;
                if (!backedUp.Contains(sourceFileName))
                {
                    Shell.Logger.Warn("Mafile {file} is not renamed: it is not in the backup archive", sourceFileName);
                    IncErrors();
                    continue;
                }

                switch (service.RenameAccountFile(MafilesDirectory, sourceFileName, targetFileName))
                {
                    case RenameAccountFileResult.SourceMissing:
                        Shell.Logger.Warn("Can't rename mafile {old} to {new} because source file not found",
                            mafile.Filename, targetFileName);
                        IncErrors();
                        continue;
                    case RenameAccountFileResult.TargetExists:
                        Shell.Logger.Warn("Can't rename mafile {old} to {new} because target file already exist",
                            mafile.Filename, targetFileName);
                        res.Conflict += 1;
                        continue;
                }

                dic[sourceFileName] = targetFileName;
                res.Renamed += 1;
                mafile.Filename = targetFileName;
            }
            catch (Exception ex)
            {
                Shell.Logger.Error(ex, "Error renaming mafile {file} {accountName}", mafile.Filename,
                    mafile.AccountName);
                IncErrors();
            }
        }

        MAACStorage.NotifyMafilesRenamed(dic);
        return res;

        void IncErrors()
        {
            res.Errors += 1;
        }
    }
}
