using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AchiesUtilities.Extensions;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using SteamLibForked.Models.Session;
using SteamLibForked.Models.SteamIds;

namespace NebulaAuth.Model.MafileExport;

public static class MafileExporter
{
    // Mafiles are UTF-8 JSON without a BOM, as in the storage: other tools may reject a BOM
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<ExportResult> ExportMafiles(IEnumerable<string> keys, MafileExportTemplate template)
    {
        if (template.Path == null || !PathIsValid(template.Path))
        {
            return new ExportResult(LocManager.GetCodeBehindOrDefault("InvalidPath", "MafileExporter.InvalidPath"));
        }

        // A relative folder is relative to the data folder (the working directory is the replaceable program folder)
        var exportPath = DataRoot.ResolveUserPath(template.Path);

        if (!Directory.Exists(exportPath))
        {
            Directory.CreateDirectory(exportPath);
        }

        if (IsNebulaUtilizedDirectory(exportPath))
        {
            return new ExportResult(LocManager.GetCodeBehindOrDefault("NebulaUtilizedDirectory",
                "MafileExporter.NebulaUtilizedDirectory"));
        }

        if (!IsAllowedToWrite(exportPath))
        {
            return new ExportResult(LocManager.GetCodeBehindOrDefault("AccessDenied", "MafileExporter.AccessDenied"));
        }

        var exported = new Dictionary<Mafile, string>();
        var notFound = new List<string>();
        var conflict = new List<string>();

        var toExport = new List<(string key, Mafile mafile)>();
        foreach (var key in keys)
        {
            SteamId? steamId = null;
            if (SteamId64.TryParse(key, out var id64))
                steamId = id64;

            var maf = Storage.MaFiles.FirstOrDefault(m => m.AccountName.EqualsIgnoreCase(key) || m.SteamId == steamId);
            if (maf != null)
                toExport.Add((key, maf));
            else
                notFound.Add(key);
        }

        if (template.ExportToZip)
        {
            if (toExport.Count > 0)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var zipName = $"{toExport.Count}_{timestamp}.zip";
                var zipPath = Path.Combine(exportPath, zipName);

                await using var zipStream = File.Create(zipPath);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
                var addedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (key, maf) in toExport)
                {
                    var (content, fileName) = BuildMafileContent(template, maf);
                    if (!addedEntries.Add(fileName))
                    {
                        conflict.Add(key);
                        continue;
                    }

                    var entry = archive.CreateEntry(fileName);
                    await using var entryStream = entry.Open();
                    await using var writer = new StreamWriter(entryStream, Utf8NoBom);
                    await writer.WriteAsync(content);
                    exported[maf] = fileName;
                }
            }
        }
        else
        {
            foreach (var (key, maf) in toExport)
            {
                var (content, fileName) = BuildMafileContent(template, maf);
                var fullPath = Path.Combine(exportPath, fileName);
                if (File.Exists(fullPath))
                {
                    conflict.Add(key);
                    continue;
                }

                await File.WriteAllTextAsync(fullPath, content, Utf8NoBom);
                exported[maf] = fullPath;
            }
        }

        return new ExportResult(exported, notFound, conflict, exportPath);
    }

    private static (string content, string fileName) BuildMafileContent(MafileExportTemplate template, Mafile mafile)
    {
        var session = template.IncludeSessionData
            ? mafile.SessionData
            : new MobileSessionData(null!, mafile.SteamId, default, null, tokens: null);

        var serializeMaf = new Mafile
        {
            SharedSecret = template.IncludeSharedSecret ? mafile.SharedSecret : null!,
            IdentitySecret = template.IncludeIdentitySecret ? mafile.IdentitySecret : null!,
            DeviceId = template.IncludeIdentitySecret ? mafile.DeviceId : null!,
            RevocationCode = template.IncludeRCode ? mafile.RevocationCode : null!,
            AccountName = mafile.AccountName,
            SessionData = session,
            SteamId = mafile.SteamId,
            ServerTime = template.IncludeOtherInfo ? mafile.ServerTime : 0,
            SerialNumber = template.IncludeOtherInfo ? mafile.SerialNumber : 0,
            Uri = template.IncludeOtherInfo ? mafile.Uri : null!,
            TokenGid = template.IncludeOtherInfo ? mafile.TokenGid : null!,
            Secret1 = template.IncludeOtherInfo ? mafile.Secret1 : null!,
            Proxy = template.IncludeNebulaProxy ? mafile.Proxy : null!,
            Group = template.IncludeNebulaGroup ? mafile.Group : null!,
            DeviceName = template.IncludeDeviceName ? mafile.DeviceName : null,
            LinkedClient = null,
            Filename = null
        };

        var serialized = NebulaSerializer.SerializeMafile(serializeMaf);
        var strategy = template.UseLoginAsMafileName ? MafileNamingStrategy.Login : MafileNamingStrategy.SteamId;
        var fileName = strategy.GetMafileName(serializeMaf);
        return (serialized, fileName);
    }

    private static bool PathIsValid(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var full = DataRoot.ResolveUserPath(path);

            var invalidPathChars = Path.GetInvalidPathChars();
            if (full.IndexOfAny(invalidPathChars) >= 0) return false;

            var invalidFileChars = Path.GetInvalidFileNameChars();

            foreach (var part in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (part is [_, ':']) continue;

                if (part.IndexOfAny(invalidFileChars) >= 0) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAllowedToWrite(string path)
    {
        try
        {
            var testFilePath = Path.Combine(path, "test.tmp");
            using (var fs = File.Create(testFilePath))
            {
            }

            File.Delete(testFilePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNebulaUtilizedDirectory(string path)
    {
        return Storage.MafilesDirectories.Any(x => DataRoot.ResolveUserPath(path).EqualsIgnoreCase(x));
    }
}