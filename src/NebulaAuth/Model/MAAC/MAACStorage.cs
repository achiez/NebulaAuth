using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.LinkHost;
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
            var droppedLinked = false;
            foreach (var (fileName, storedClient) in clients)
            {
                var mafile = Storage.MaFiles.FirstOrDefault(x => x.Filename == fileName);
                if (mafile == null) continue;
                if (storedClient is {AutoConfirmMarket: false, AutoConfirmTrades: false}) continue;
                // Accounts connected through Nebula Link are never auto-confirmed; their saved flags are dropped for good (they are
                // not restored when the account is unlinked later)
                if (NebulaLinkHost.IsLinked(mafile))
                {
                    droppedLinked = true;
                    continue;
                }

                if (MultiAccountAutoConfirmer.TryAddToConfirm(mafile) && mafile.LinkedClient != null)
                {
                    mafile.LinkedClient.AutoConfirmMarket = storedClient.AutoConfirmMarket;
                    mafile.LinkedClient.AutoConfirmTrades = storedClient.AutoConfirmTrades;
                    mafile.LinkedClient.PropertyChanged += LinkedClientOnPropertyChanged;
                    Clients[fileName] = storedClient;
                }
            }

            // The restored flags (and the dropped entries of linked accounts) are written back now
            if (droppedLinked || Clients.Count > 0) Save();
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Failed to load MAAC storage");
            SnackbarController.SendSnackbar(
                LocManager.GetCodeBehindOrDefault("FailedToLoadStorage", "MAAC", "FailedToLoadStorage"));
        }
    }

    /// <summary>
    ///     Mafiles were renamed: the stored flags follow the new file names. Entries of files that were not renamed are
    ///     kept as they are.
    /// </summary>
    public static void NotifyMafilesRenamed(IDictionary<string, string> oldNewNames)
    {
        var updatedClients = new Dictionary<string, StoredClient>(Clients);
        var moved = new List<(string NewName, StoredClient Client)>();
        foreach (var (oldName, newName) in oldNewNames)
        {
            if (updatedClients.Remove(oldName, out var storedClient)) moved.Add((newName, storedClient));
        }

        // Added after all removals: a rename chain (a→b, b→c) must not drop an entry
        foreach (var (newName, storedClient) in moved) updatedClients[newName] = storedClient;
        Clients = updatedClients;
        Save();
    }

    /// <summary>
    ///     The account was connected through Nebula Link: auto-confirmation is turned off and its saved flags are forgotten (not
    ///     restored when the account is unlinked; the user enables it again explicitly).
    /// </summary>
    public static void ForgetAccount(Mafile mafile)
    {
        if (Avalonia.Application.Current != null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ForgetAccount(mafile));
            return;
        }

        if (mafile.LinkedClient != null || MultiAccountAutoConfirmer.Clients.Contains(mafile))
            MultiAccountAutoConfirmer.RemoveFromConfirm(mafile);
        if (mafile.Filename != null && Clients.Remove(mafile.Filename)) Save();
    }

    private class StoredClient
    {
        public bool AutoConfirmMarket { get; set; }
        public bool AutoConfirmTrades { get; set; }
    }
}