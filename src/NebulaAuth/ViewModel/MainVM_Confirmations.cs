using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.Utility;
using SteamLib.SteamMobile.Confirmations;

namespace NebulaAuth.ViewModel;

public partial class MainVM //Confirmations
{
    public ObservableCollection<Confirmation> Confirmations { get; } = [];
    public bool ConfirmationsVisible => SelectedMafile == _confirmationsLoadedForMafile;
    private Mafile? _confirmationsLoadedForMafile;

    /// <summary>
    ///     Loads the list of the account selected when the command started (a selection change meanwhile does not mix
    ///     accounts). Reads go through <see cref="ConfirmationService" />: the same snapshot, per-account order and history
    ///     as Achiefy requests.
    /// </summary>
    [RelayCommand]
    private async Task GetConfirmations()
    {
        var maf = SelectedMafile;
        if (maf == null) return;
        List<Confirmation> conf;
        try
        {
            var snapshot = await NebulaLinkHost.Confirmations.GetAsync(maf, SnapshotPolicy.Manual,
                SessionMode.Interactive, CancellationToken.None);
            conf = snapshot.Items.ToList();
        }
        catch (Exception ex)
            when (ExceptionHandler.Handle(ex))
        {
            return;
        }

        // The user selected another account while the list was loading: the list belongs to the old one
        if (SelectedMafile != maf) return;

        _confirmationsLoadedForMafile = maf;
        OnPropertyChanged(nameof(ConfirmationsVisible));
        Confirmations.Clear();
        var marketConfirmations = conf
            .Where(c => c.ConfType == ConfirmationType.MarketSellTransaction)
            .OfType<MarketConfirmation>()
            .ToList();

        if (marketConfirmations.Count > 1)
        {
            var indexOfLast = conf.IndexOf(marketConfirmations.First());
            foreach (var mCon in marketConfirmations)
            {
                conf.Remove(mCon);
            }

            var mConf = new MarketMultiConfirmation(marketConfirmations);
            conf.Insert(indexOfLast, mConf);
        }

        foreach (var con in conf)
        {
            Confirmations.Add(con);
        }
    }

    [RelayCommand]
    private Task Confirm(Confirmation? confirmation)
    {
        var maf = _confirmationsLoadedForMafile ?? SelectedMafile;
        if (maf == null || confirmation == null) return Task.CompletedTask;
        return SendConfirmation(maf, confirmation, true);
    }

    [RelayCommand]
    private Task Cancel(Confirmation? confirmation)
    {
        var maf = _confirmationsLoadedForMafile ?? SelectedMafile;
        if (maf == null || confirmation == null) return Task.CompletedTask;
        return SendConfirmation(maf, confirmation, false);
    }


    private async Task SendConfirmation(Mafile mafile, Confirmation confirmation, bool confirm)
    {
        bool result;
        IReadOnlyList<Confirmation> items = confirmation is MarketMultiConfirmation multi
            ? multi.Confirmations.Cast<Confirmation>().ToList()
            : [confirmation];

        try
        {
            result = await NebulaLinkHost.Confirmations.SendManualAsync(mafile, items, confirm, CancellationToken.None);
        }
        catch (Exception ex)
            when (ExceptionHandler.Handle(ex))
        {
            return;
        }

        if (!result)
        {
            SnackbarController.SendSnackbar(GetLocalization("ConfirmationError"));
            return;
        }

        UpdateConfirmationState(confirmation);
    }

    private void UpdateConfirmationState(Confirmation confirmation)
    {
        if (confirmation is MarketConfirmation item)
        {
            var parent = Confirmations
                .OfType<MarketMultiConfirmation>()
                .FirstOrDefault(p => p.Confirmations.Contains(item));

            if (parent == null)
            {
                Confirmations.Remove(item);
                return;
            }

            parent.Confirmations.Remove(item);

            if (parent.Confirmations.Count == 0)
            {
                Confirmations.Remove(parent);
                return;
            }

            if (parent.Confirmations.Count == 1)
            {
                var remaining = parent.Confirmations[0];
                var idx = Confirmations.IndexOf(parent);
                Confirmations[idx] = remaining;
                return;
            }

            parent.Time = parent.Confirmations.FirstOrDefault()?.Time ?? parent.Time;
            return;
        }

        Confirmations.Remove(confirmation);
    }
}
