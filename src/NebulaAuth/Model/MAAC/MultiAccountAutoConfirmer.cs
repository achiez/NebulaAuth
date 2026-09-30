using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Extensions;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;

namespace NebulaAuth.Model.MAAC;

public static class MultiAccountAutoConfirmer
{
    private const string LOC_PATH = "MAAC";
    private static readonly ReaderWriterLockSlim Lock = new();

    private static readonly SemaphoreSlim ExecutionLock = new(1, 1);
    public static ObservableCollection<Mafile> Clients { get; }
    private static Timer Timer { get; }

    static MultiAccountAutoConfirmer()
    {
        Clients = [];
        Timer = new Timer(TimerConfirm);
        Settings.Instance.PropertyChanged += SettingsOnPropertyChanged;
        UpdateTimer(true);
    }

    // ReSharper disable once AsyncVoidMethod //Already safe
    private static async void TimerConfirm(object? state)
    {
        var isHeld = false;
        try
        {
            isHeld = await ExecutionLock.WaitAsync(0);
            if (!isHeld)
            {
                SnackbarController.SendSnackbar(GetLocalization("TimerPreventedOverlap"));
                return;
            }

            await TimerConfirmInternal();
        }
        catch (Exception e)
        {
            Shell.Logger.Error(e, "Error in MAAC timer");
        }
        finally
        {
            if (isHeld)
            {
                ExecutionLock.Release();
            }
        }
    }

    private static async Task TimerConfirmInternal()
    {
        var enabledClients = GetReadyAccounts();
        enabledClients = DistributeEvenly(enabledClients).ToArray();
        var confirmed = 0;
        await Task.Run(async () =>
        {
            foreach (var client in enabledClients)
            {
                var conf = 0;
                try
                {
                    conf = await MAACRequestHandler.HandleRequest(client.LinkedClient!,
                        () => client.LinkedClient!.Confirm());
                    MaacErrorLog.Instance.Success(client.AccountName);
                }
                catch (ObjectDisposedException)
                {
                    //Ignored
                }
                catch (Exception ex)
                {
                    // A dead proxy fails on every tick for every account using it: the first failure is logged in
                    // full, repeats are summarized
                    var proxy = client.Proxy?.Data ?? MaClient.DefaultProxy;
                    var proxyLabel = client.Proxy != null ? $"proxy #{client.Proxy.Id}"
                        : proxy != null ? "default proxy" : "no proxy";
                    MaacErrorLog.Instance.Failure(client.AccountName, proxyLabel, ex, [proxy?.Password]);
                }

                confirmed += conf;
            }
        }).ConfigureAwait(false);
        if (confirmed > 0)
            SnackbarController.SendSnackbar(GetLocalization("TimerConfirmed") + confirmed);
        return;

        //This function helps us to prevent 429 as much as it possible.
        //It's not perfect but better than nothing
        static IEnumerable<Mafile> DistributeEvenly(IEnumerable<Mafile> input)
        {
            var elementCounts = input
                .GroupBy(x => x.Proxy?.Id ?? -1)
                .ToDictionary(g => g.Key, g => new Queue<Mafile>(g));

            var result = new List<Mafile>();
            bool added;
            do
            {
                added = false;
                foreach (var key in elementCounts.Keys.ToList())
                {
                    if (elementCounts[key].Count > 0)
                    {
                        result.Add(elementCounts[key].Dequeue());
                        added = true;
                    }
                }
            } while (added);

            return result;
        }
    }

    private static IEnumerable<Mafile> GetReadyAccounts()
    {
        var clients = Lock.ReadLock(() => Clients.ToArray());
        var res = new List<Mafile>();
        foreach (var maf in clients)
        {
            if (maf.LinkedClient == null) continue;
            if (MAACRequestHandler.IsReady(maf))
            {
                res.Add(maf);
            }
        }

        return res;
    }

    public static bool TryAddToConfirm(Mafile mafile)
    {
        return Lock.WriteLock(() =>
        {
            if (Clients.Contains(mafile)) return false;
            mafile.LinkedClient = new PortableMaClient(mafile);
            Clients.Add(mafile);
            MAACRequestHandler.Register(mafile);
            return true;
        });
    }


    public static void RemoveFromConfirm(Mafile mafile)
    {
        Lock.WriteLock(() =>
        {
            Clients.Remove(mafile);
            mafile.LinkedClient?.Dispose();
            mafile.LinkedClient = null;
            MAACRequestHandler.Unregister(mafile);
        });
    }


    private static void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Settings.TimerSeconds)) return;
        UpdateTimer();
    }

    private static void UpdateTimer(bool firstStart = false)
    {
        var timerInterval = Settings.Instance.TimerSeconds;
        var intervalTimeSpan = TimeSpan.FromSeconds(timerInterval);
        var dueTime = intervalTimeSpan;
        if (firstStart)
        {
            dueTime = timerInterval >= 30 ? intervalTimeSpan : TimeSpan.FromSeconds(30);
        }

        Timer.Change(dueTime, intervalTimeSpan);
    }

    private static string GetLocalization(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, LOC_PATH, key);
    }
}