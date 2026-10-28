using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NebulaAuth.Model.Mafiles.Protection;

/// <summary>
///     Content-level helpers of the storage layer: removal of the legacy Steam "Password" field from mafile JSON (also
///     inside backup ZIPs) and comparison of two versions of one file. Works on JSON, not on the mafile model, so files
///     that are not complete mafiles (backups) keep all their other data unchanged.
/// </summary>
public static class StorageContent
{
    /// <summary>Legacy field of 1.x mafiles with the encrypted Steam password (removed from all files in 2.x).</summary>
    public const string LEGACY_PASSWORD_PROPERTY = "Password";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    ///     Parses mafile JSON; null when it is not a JSON object.
    /// </summary>
    public static JObject? TryParseObject(byte[] bytes)
    {
        try
        {
            var text = Utf8NoBom.GetString(bytes).TrimStart('﻿');
            using var reader = new JsonTextReader(new StringReader(text))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal
            };
            var token = JToken.ReadFrom(reader);
            // Nothing but whitespace may follow the object
            if (reader.Read()) return null;
            return token as JObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool HasLegacyPassword(JObject j)
    {
        return j.Properties().Any(IsPasswordProperty);
    }

    private static bool IsPasswordProperty(JProperty p)
    {
        return string.Equals(p.Name, LEGACY_PASSWORD_PROPERTY, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Removes the top-level "Password" field. Returns the new bytes, or the same array when there was no such field.
    /// </summary>
    /// <exception cref="FormatException">Not a JSON object.</exception>
    public static byte[] StripPassword(byte[] mafileJson, out bool changed)
    {
        var j = TryParseObject(mafileJson) ?? throw new FormatException("The file is not a JSON object");
        changed = false;
        foreach (var p in j.Properties().Where(IsPasswordProperty).ToList())
        {
            p.Remove();
            changed = true;
        }

        return changed ? Utf8NoBom.GetBytes(j.ToString(Formatting.Indented)) : mafileJson;
    }

    /// <summary>
    ///     Same content ignoring the legacy password field and formatting. Bytes are compared when either side is not JSON.
    /// </summary>
    public static bool MafilesEquivalent(byte[] a, byte[] b)
    {
        if (a.AsSpan().SequenceEqual(b)) return true;
        var ja = TryParseObject(a);
        var jb = TryParseObject(b);
        if (ja == null || jb == null) return false;
        foreach (var p in ja.Properties().Where(IsPasswordProperty).ToList()) p.Remove();
        foreach (var p in jb.Properties().Where(IsPasswordProperty).ToList()) p.Remove();
        return JToken.DeepEquals(ja, jb);
    }

    #region Zip

    /// <summary>
    ///     Reads all entries of a ZIP archive (validates it). Null when it is not a readable ZIP.
    /// </summary>
    public static List<ZipItem>? TryReadZip(byte[] zipBytes)
    {
        try
        {
            using var ms = new MemoryStream(zipBytes, false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read, false);
            var result = new List<ZipItem>();
            foreach (var entry in archive.Entries)
            {
                using var es = entry.Open();
                using var buffer = new MemoryStream();
                es.CopyTo(buffer);
                result.Add(new ZipItem(entry.FullName, entry.LastWriteTime, buffer.ToArray()));
            }

            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static byte[] CreateZip(IEnumerable<ZipItem> items)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var item in items)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = item.LastWriteTime;
                using var es = entry.Open();
                es.Write(item.Content);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    ///     Removes the legacy password field from every mafile entry of a backup ZIP. Returns the same array when nothing
    ///     changed. Entries that are not JSON objects are kept as they are.
    /// </summary>
    /// <exception cref="FormatException">Not a readable ZIP.</exception>
    public static byte[] StripPasswordsInZip(byte[] zipBytes, out bool changed)
    {
        var items = TryReadZip(zipBytes) ?? throw new FormatException("The file is not a readable ZIP archive");
        changed = false;
        var result = new List<ZipItem>(items.Count);
        foreach (var item in items)
        {
            var content = item.Content;
            if (item.Name.EndsWith(StorageFileNames.MAFILE_EXTENSION, StringComparison.OrdinalIgnoreCase) &&
                TryParseObject(content) is { } j && HasLegacyPassword(j))
            {
                content = StripPassword(content, out _);
                changed = true;
            }

            result.Add(item with {Content = content});
        }

        return changed ? CreateZip(result) : zipBytes;
    }

    /// <summary>
    ///     Same entries with equivalent content (mafile entries compared ignoring the legacy password field).
    /// </summary>
    public static bool ZipsEquivalent(byte[] a, byte[] b)
    {
        if (a.AsSpan().SequenceEqual(b)) return true;
        var za = TryReadZip(a);
        var zb = TryReadZip(b);
        if (za == null || zb == null || za.Count != zb.Count) return false;
        var byName = zb.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var item in za)
        {
            if (!byName.TryGetValue(item.Name, out var other)) return false;
            var equal = item.Name.EndsWith(StorageFileNames.MAFILE_EXTENSION, StringComparison.OrdinalIgnoreCase)
                ? MafilesEquivalent(item.Content, other.Content)
                : item.Content.AsSpan().SequenceEqual(other.Content);
            if (!equal) return false;
        }

        return true;
    }

    #endregion
}

public sealed record ZipItem(string Name, DateTimeOffset LastWriteTime, byte[] Content);
