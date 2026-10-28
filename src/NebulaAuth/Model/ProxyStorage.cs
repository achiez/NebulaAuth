using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AchiesUtilities.Collections;
using AchiesUtilities.Web.Proxy;
using AchiesUtilities.Web.Proxy.Parsing;
using NebulaAuth.Core;
using Newtonsoft.Json;

namespace NebulaAuth.Model;

public static class ProxyStorage
{
    public const string FORMAT = ADDRESS_FORMAT + ":{USER}:{PASS}";
    public const string ADDRESS_FORMAT = "{IP}:{PORT}";

    public static readonly ProxyParser DefaultScheme = new(
        ProxyDefaultFormats.UniversalColon, false, ProxyProtocol.HTTP,
        ProxyPatternProtocol.All,
        ProxyPatternHostFormat.Domain | ProxyPatternHostFormat.IPv4,
        PatternRequirement.Optional,
        PatternRequirement.Optional);

    public static readonly ProxyParser SignAtScheme = new(
        ProxyDefaultFormats.UniversalSignAt, false, ProxyProtocol.HTTP,
        ProxyPatternProtocol.All,
        ProxyPatternHostFormat.Domain | ProxyPatternHostFormat.IPv4,
        PatternRequirement.Required,
        PatternRequirement.Required);


    public static ObservableDictionary<int, ProxyData> Proxies { get; } = new();


    private static string FilePath => DataRoot.Combine("proxies.json");

    static ProxyStorage()
    {
        if (!File.Exists(FilePath))
            return;
        try
        {
            var json = File.ReadAllText(FilePath);
            var proxies = JsonConvert.DeserializeObject<ProxiesSchema>(json) ?? throw new NullReferenceException();
            Proxies = proxies.ProxiesData;
            Proxies = new ObservableDictionary<int, ProxyData>(
                Proxies.OrderBy(p => p.Key)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

            if (proxies.DefaultProxy != null)
            {
                MaClient.DefaultProxy = Proxies[proxies.DefaultProxy.Value];
            }
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Error while loading proxies");
            SnackbarController.SendSnackbar(LocManager.GetCodeBehindOrDefault("Error when loading proxies",
                "ProxyStorage", "ErrorWhileLoadingProxies"));
            SnackbarController.SendSnackbar(ex.Message);
        }
    }

    public static void SetProxy(int? id, ProxyData proxyData)
    {
        if (id == null)
        {
            if (Proxies.Count == 0)
            {
                id = 0;
            }
            else
            {
                id = Proxies.Keys.Max() + 1;
            }
        }

        Proxies[id] = proxyData;

        Save();
    }

    public static void SetProxies(IEnumerable<KeyValuePair<int?, ProxyData>> proxies)
    {
        foreach (var (key, proxyData) in proxies)
        {
            var id = key;
            if (id == null)
            {
                if (Proxies.Count == 0)
                {
                    id = 0;
                }
                else
                {
                    id = Proxies.Keys.Max() + 1;
                }
            }

            Proxies[id] = proxyData;
        }

        Save();
    }

    public static void SortCollection() //RETHINK: maybe there is a better way to handle it
    {
        var proxies = Proxies.OrderBy(p => p.Key)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Proxies.Clear();
        foreach (var kvp in proxies)
        {
            Proxies.Add(kvp.Key, kvp.Value);
        }
    }

    public static void RemoveProxy(int id)
    {
        Proxies.Remove(id);
        Save();
    }

    public static void Save()
    {
        var p = Create();
        var json = JsonConvert.SerializeObject(p, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    public static string GetProxyString(ProxyData proxyData)
    {
        return proxyData.AuthEnabled ? proxyData.ToString(FORMAT) : proxyData.ToString(ADDRESS_FORMAT);
    }

    private static ProxiesSchema Create()
    {
        int? def = null;
        if (MaClient.DefaultProxy != null)
        {
            var search = Proxies.FirstOrDefault(p => p.Value.Equals(MaClient.DefaultProxy));
            if (search.Value != null!)
            {
                def = search.Key;
            }
        }

        return new ProxiesSchema
        {
            ProxiesData = Proxies,
            DefaultProxy = def
        };
    }

    private class ProxiesSchema
    {
        public int? DefaultProxy;
        public ObservableDictionary<int, ProxyData> ProxiesData = [];
    }
}