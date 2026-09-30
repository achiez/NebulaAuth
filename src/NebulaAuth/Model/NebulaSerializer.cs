using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Exceptions;
using NebulaAuth.Model.Mafiles.Protection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamLib;
using SteamLib.Utility.MafileSerialization;

namespace NebulaAuth.Model;

public static class NebulaSerializer
{
    public static MafileSerializer Serializer { get; }

    static NebulaSerializer()
    {
        Serializer = new MafileSerializer(new MafileSerializerSettings
        {
            DeserializationOptions =
            {
                AllowDeviceIdGeneration = true,
                AllowSessionIdGeneration = true,
                ThrowIfInvalidSteamId = false
            },
            SerializationOptions =
            {
                Sign = true,
                Formatting = Formatting.Indented
            }
        });
    }


    public static Mafile Deserialize(string cont, string path)
    {
        return Deserialize(cont, path, out _);
    }

    /// <summary>
    ///     Reads a mafile. The legacy "Password" field (Steam password of 1.x versions, third-party mafiles) is never
    ///     read: Steam passwords live only in the encrypted password store.
    /// </summary>
    /// <param name="cont">JSON.</param>
    /// <param name="path">Path or name of the file (sets <see cref="Mafile.Filename" />).</param>
    /// <param name="hasLegacyPassword">The JSON contains the legacy "Password" field (to be removed from the file).</param>
    public static Mafile Deserialize(string cont, string path, out bool hasLegacyPassword)
    {
        var data = Serializer.Deserialize(cont);
        var mobileData = data.Data;
        var info = data.Info;
        if (!info.IsExtended)
            throw new FormatException("Mafile is not extended data");


        var props = info.UnusedProperties ?? [];
        hasLegacyPassword = props.Keys.Any(k =>
            string.Equals(k, StorageContent.LEGACY_PASSWORD_PROPERTY, StringComparison.OrdinalIgnoreCase));
        var proxy = GetPropertyValue<MaProxy>("Proxy", props);
        var group = GetPropertyValue<string>("Group", props);
        var mafile = Mafile.FromMobileDataExtended((MobileDataExtended) mobileData, proxy, group);

        mafile.Filename = Path.GetFileName(path);

        if (!info.SteamIdValid)
            throw new MafileNeedReloginException(mafile);

        return mafile;
    }

    private static T? GetPropertyValue<T>(string name, Dictionary<string, JProperty> dictionary)
    {
        if (!dictionary.TryGetValue(name, out var prop)) return default;
        var value = prop.Value;
        try
        {
            return value.ToObject<T>();
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "Can't deserialize property {name}", name);
            return default;
        }
    }

    public static string SerializeMafile(Mafile data)
    {
        var props = new Dictionary<string, object?>
        {
            {nameof(Mafile.Proxy), data.Proxy},
            {nameof(Mafile.Group), data.Group}
        };
        return SerializeMafile(data, props);
    }


    public static string SerializeMafile(MobileDataExtended data, Dictionary<string, object?>? properties)
    {
        return MafileSerializer.SerializeLegacy(data, Serializer.Settings.SerializationOptions, properties);
    }
}