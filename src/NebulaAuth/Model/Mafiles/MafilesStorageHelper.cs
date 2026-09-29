using System;
using System.IO;
using NebulaAuth.Model.Entities;

namespace NebulaAuth.Model.Mafiles;

internal static class MafilesStorageHelper
{
    /// <summary>
    ///     Returns <see cref="Mafile.Filename" /> or creates a new one and updates the property.
    ///     <see cref="Mafile.Filename" /> is always not <see langword="null" /> after this method call.
    ///     A new name reuses an existing file that differs only in case (e.g. SDA "*.maFile").
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public static string GetOrUpdateMafilePath(Mafile data)
    {
        if (data.Filename != null)
        {
            return Path.Combine(Storage.MafilesDirectory, data.Filename);
        }

        var fileName = CreateMafileFileName(data, Settings.Instance.UseAccountNameAsMafileName);
        fileName = FindExistingMafileName(fileName) ?? fileName;
        data.Filename = fileName;
        return Path.Combine(Storage.MafilesDirectory, fileName);
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
    ///     Actual name of the file in <see cref="Storage.MafilesDirectory" /> that matches <paramref name="fileName" />
    ///     ignoring case, or <see langword="null" /> when there is none. Mafiles are loaded with any extension case, and
    ///     on case-sensitive file systems (Linux) an exact lookup of the generated name misses them.
    /// </summary>
    public static string? FindExistingMafileName(string fileName)
    {
        foreach (var mafile in Storage.MaFiles)
        {
            if (mafile.Filename != null && mafile.Filename.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(Storage.MafilesDirectory, mafile.Filename)))
                return mafile.Filename;
        }

        if (!Directory.Exists(Storage.MafilesDirectory)) return null;
        foreach (var path in Directory.EnumerateFiles(Storage.MafilesDirectory))
        {
            var name = Path.GetFileName(path);
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return name;
        }

        return null;
    }
}