using System;
using System.IO;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles.Protection;

namespace NebulaAuth.Model.Mafiles;

internal static class MafilesStorageHelper
{
    /// <summary>
    ///     Returns <see cref="Mafile.Filename" /> (logical name, "*.mafile" in every storage mode) or creates a new one and
    ///     updates the property. <see cref="Mafile.Filename" /> is always not <see langword="null" /> after this method
    ///     call. A new name reuses an existing file that differs only in case (e.g. SDA "*.maFile").
    /// </summary>
    public static string GetOrUpdateMafileName(Mafile data)
    {
        if (data.Filename != null)
        {
            return data.Filename;
        }

        var fileName = CreateMafileFileName(data, Settings.Instance.UseAccountNameAsMafileName);
        fileName = FindExistingMafileName(fileName) ?? fileName;
        data.Filename = fileName;
        return fileName;
    }

    /// <summary>
    ///     Creates mafile file name according to the current settings.
    /// </summary>
    /// <param name="data"></param>
    /// <param name="useAccountName"></param>
    /// <returns></returns>
    public static string CreateMafileFileName(Mafile data, bool useAccountName)
    {
        return useAccountName
            ? MafileNamingStrategy.Login.GetMafileName(data)
            : MafileNamingStrategy.SteamId.GetMafileName(data);
    }

    /// <summary>
    ///     Logical name of the mafile in <see cref="Storage.MafilesDirectory" /> (plain or encrypted) that matches
    ///     <paramref name="fileName" /> ignoring case, or <see langword="null" /> when there is none. Mafiles are loaded
    ///     with any extension case, and on case-sensitive file systems (Linux) an exact lookup of the generated name
    ///     misses them.
    /// </summary>
    public static string? FindExistingMafileName(string fileName)
    {
        var service = MafileStorageService.Current;
        foreach (var mafile in Storage.MaFiles)
        {
            if (mafile.Filename != null && mafile.Filename.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                service.AccountFileExists(Storage.MafilesDirectory, mafile.Filename))
                return mafile.Filename;
        }

        if (!Directory.Exists(Storage.MafilesDirectory)) return null;
        foreach (var path in Directory.EnumerateFiles(Storage.MafilesDirectory))
        {
            var name = Path.GetFileName(path);
            if (!StorageFileNames.IsMafile(StorageFileNames.Classify(name))) continue;
            var logical = StorageFileNames.ToPlainName(name);
            if (logical.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return logical;
        }

        return null;
    }
}