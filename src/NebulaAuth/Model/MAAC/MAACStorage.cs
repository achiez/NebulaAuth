using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using Newtonsoft.Json;

namespace NebulaAuth.Model.MAAC;

public static class MAACStorage
{
    private static Dictionary<string, StoredClient> Clients { get; set; } = [];

    static MAACStorage()
    {
    }

    private static void ClientsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Clients.Clear();
        }
        else if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is Mafile {Filename: not null} mafile)
                {
                    if (mafile.LinkedClient != null)
                        mafile.LinkedClient.PropertyChanged += LinkedClientOnPropertyChanged;

                    Clients.Add(mafile.Filename, new StoredClient
                    {
                        AutoConfirmMarket = mafile.LinkedClient?.AutoConfirmMarket ?? false,
                        AutoConfirmTrades = mafile.LinkedClient?.AutoConfirmTrades ?? false
                    });
                }
            }
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is Mafile {Filename: not null} mafile)
                {
                    if (mafile.LinkedClient != null)
                        mafile.LinkedClient.PropertyChanged -= LinkedClientOnPropertyChanged;

                    Clients.Remove(mafile.Filename);
                }
            }
        }

        Save();
    }

    private static void LinkedClientOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PortableMaClient client) return;
        if (client.Mafile.Filename == null) return;
        var anyChanges = false;
        if (!Clients.TryGetValue(client.Mafile.Filename, out var storedClient))
        {
            client.PropertyChanged -= LinkedClientOnPropertyChanged;
            return;
        }

        if (e.PropertyName == nameof(PortableMaClient.AutoConfirmMarket))
        {
            anyChanges = storedClient.AutoConfirmMarket != client.AutoConfirmMarket;
            storedClient.AutoConfirmMarket = client.AutoConfirmMarket;
        }
        else if (e.PropertyName == nameof(PortableMaClient.AutoConfirmTrades))
        {
            anyChanges = storedClient.AutoConfirmTrades != client.AutoConfirmTrades;
            storedClient.AutoConfirmTrades = client.AutoConfirmTrades;
        }

        if (anyChanges)
            Save();
    }

    private static string FilePath => DataRoot.Combine("maac.json");

    public static void Save()
    {
        var json = JsonConvert.SerializeObject(Clients, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    public static void Initialize()
    {
        MultiAccountAutoConfirmer.Clients.CollectionChanged += ClientsOnCollectionChanged;
        if (!File.Exists(FilePath)) return;
        try
        {
            var json = File.ReadAllText(FilePath);
            var clients = JsonConvert.DeserializeObject<Dictionary<string, StoredClient>>(json) ?? [];
            foreach (var (fileName, storedClient) in clients)
            {
                var mafile = Storage.MaFiles.FirstOrDefault(x => x.Filename == fileName);
                if (mafile == null) continue;
                if (storedClient is {AutoConfirmMarket: false, AutoConfirmTrades: false}) continue;
                if (MultiAccountAutoConfirmer.TryAddToConfirm(mafile) && mafile.LinkedClient != null)
                {
                    mafile.LinkedClient.AutoConfirmMarket = storedClient.AutoConfirmMarket;
                    mafile.LinkedClient.AutoConfirmTrades = storedClient.AutoConfirmTrades;
                    mafile.LinkedClient.PropertyChanged += LinkedClientOnPropertyChanged;
                    Clients[fileName] = storedClient;
                }
            }
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Failed to load MAAC storage");
            SnackbarController.SendSnackbar(
                LocManager.GetCodeBehindOrDefault("FailedToLoadStorage", "MAAC", "FailedToLoadStorage"));
        }
    }

    public static void NotifyMafilesRenamed(IDictionary<string, string> oldNewNames)
    {
        var updatedClients = new Dictionary<string, StoredClient>();
        foreach (var (oldName, newName) in oldNewNames)
        {
            if (Clients.Remove(oldName, out var storedClient))
            {
                updatedClients[newName] = storedClient;
            }
        }

        Clients = updatedClients;
        Save();
    }

    private class StoredClient
    {
        public bool AutoConfirmMarket { get; set; }
        public bool AutoConfirmTrades { get; set; }
    }
}