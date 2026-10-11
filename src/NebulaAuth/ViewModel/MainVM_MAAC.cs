using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.Model.MAAC;

namespace NebulaAuth.ViewModel;

public partial class MainVM //MAAC
{
    public bool MaacDisplay
    {
        get => _maacDisplay;
        set
        {
            if (SetProperty(ref _maacDisplay, value))
            {
                SwitchMAACDisplay(value);
            }
        }
    }

    public bool MarketTimerEnabled
    {
        get => SelectedMafile?.LinkedClient?.AutoConfirmMarket ?? false;
        set => SetMarketTimer(value);
    }

    public bool TradeTimerEnabled
    {
        get => SelectedMafile?.LinkedClient?.AutoConfirmTrades ?? false;
        set => SetTradeTimer(value);
    }

    public int TimerCheckSeconds
    {
        get => Settings.Instance.TimerSeconds;
        set => SetTimer(value);
    }

    private bool _maacDisplay;

    /// <summary>The selected account is connected through Nebula Link: its auto-confirmation is off and can't be turned on.</summary>
    public bool IsSelectedLinked => NebulaLinkHost.IsLinked(SelectedMafile);

    public bool CanUseMaac => IsMafileSelected && !IsSelectedLinked;

    public string MarketTimerTip => LocManager.GetOrDefault("Auto-confirm market", "MainWindow", "AppBar", "MarketTimerHint");

    public string TradeTimerTip => LocManager.GetOrDefault("Auto-confirm trades", "MainWindow", "AppBar", "TradeTimerHint");

    /// <summary>Link state of the selected account may have changed (Nebula Link, selection).</summary>
    private void NotifyMaacAvailability()
    {
        OnPropertyChanged(nameof(IsSelectedLinked));
        OnPropertyChanged(nameof(CanUseMaac));
        OnPropertyChanged(nameof(MarketTimerTip));
        OnPropertyChanged(nameof(TradeTimerTip));
        OnPropertyChanged(nameof(MarketTimerEnabled));
        OnPropertyChanged(nameof(TradeTimerEnabled));
    }

    private void SetMarketTimer(bool value)
    {
        var selectedMafile = SelectedMafile;
        if (selectedMafile == null) return;
        if (value && NebulaLinkHost.IsLinked(selectedMafile))
        {
            OnPropertyChanged(nameof(MarketTimerEnabled));
            return;
        }

        if (value && selectedMafile.LinkedClient == null)
        {
            MultiAccountAutoConfirmer.TryAddToConfirm(selectedMafile);
        }

        if (!value && selectedMafile is {LinkedClient.AutoConfirmTrades: false})
        {
            MultiAccountAutoConfirmer.RemoveFromConfirm(selectedMafile);
        }

        if (selectedMafile.LinkedClient != null)
        {
            selectedMafile.LinkedClient.AutoConfirmMarket = value;
        }
    }

    private void SetTradeTimer(bool value)
    {
        var selectedMafile = SelectedMafile;
        if (selectedMafile == null) return;
        if (value && NebulaLinkHost.IsLinked(selectedMafile))
        {
            OnPropertyChanged(nameof(TradeTimerEnabled));
            return;
        }

        if (value && selectedMafile.LinkedClient == null)
        {
            MultiAccountAutoConfirmer.TryAddToConfirm(selectedMafile);
        }

        if (!value && selectedMafile is {LinkedClient.AutoConfirmMarket: false})
        {
            MultiAccountAutoConfirmer.RemoveFromConfirm(selectedMafile);
        }

        if (selectedMafile.LinkedClient != null)
        {
            selectedMafile.LinkedClient.AutoConfirmTrades = value;
        }
    }

    private void SetTimer(int value)
    {
        var timerCheckSeconds = Settings.TimerSeconds;
        if (timerCheckSeconds == value) return;
        if (timerCheckSeconds < 5)
        {
            timerCheckSeconds = 5; //Guard
        }

        if (value < 5)
        {
            value = timerCheckSeconds;
            SnackbarController.SendSnackbar(GetLocalization("TimerTooFast"));
        }

        Settings.TimerSeconds = value;
        OnPropertyChanged(nameof(TimerCheckSeconds));
        if (timerCheckSeconds != TimerCheckSeconds)
            SnackbarController.SendSnackbar(GetLocalization("TimerChanged"));
    }

    [RelayCommand]
    private void SwitchMAAC(bool market)
    {
        var maf = SelectedMafile;
        if (maf == null) return;
        SwitchMAACOn([maf], market);
    }

    [RelayCommand]
    private void SwitchMAACOnGroup(bool market)
    {
        var group = SelectedGroup;
        if (group == null) return;
        var mafilesInGroup = MaFiles.Where(m => group.Equals(m.Group));
        SwitchMAACOn(mafilesInGroup, market);
    }

    [RelayCommand]
    private void SwitchMAACOnAll(bool market)
    {
        SwitchMAACOn(MaFiles, market);
    }

    private void SwitchMAACOn(IEnumerable<Mafile> mafiles, bool market)
    {
        mafiles = mafiles.ToArray();

        var turnOn = mafiles.All(m => m.LinkedClient == null || !GetCurrentMode(m.LinkedClient));
        if (turnOn)
        {
            foreach (var mafile in mafiles)
            {
                MultiAccountAutoConfirmer.TryAddToConfirm(mafile);
                SetCurrentMode(mafile.LinkedClient, turnOn);
            }
        }
        else
        {
            foreach (var mafile in mafiles)
            {
                SetCurrentMode(mafile.LinkedClient, turnOn);
                if (PretendsToRemove(mafile))
                    MultiAccountAutoConfirmer.RemoveFromConfirm(mafile);
            }
        }

        return;

        bool PretendsToRemove(Mafile mafile)
        {
            return mafile.LinkedClient is {AutoConfirmMarket: false, AutoConfirmTrades: false};
        }

        bool GetCurrentMode(PortableMaClient linkedClient)
        {
            return market ? linkedClient.AutoConfirmMarket : linkedClient.AutoConfirmTrades;
        }

        void SetCurrentMode(PortableMaClient? linkedClient, bool value)
        {
            if (linkedClient == null) return;
            if (market)
            {
                linkedClient.AutoConfirmMarket = value;
            }
            else
            {
                linkedClient.AutoConfirmTrades = value;
            }
        }
    }

    private void SwitchMAACDisplay(bool newValue)
    {
        if (newValue)
        {
            MaFiles = MultiAccountAutoConfirmer.Clients;
        }
        else
        {
            PerformQuery();
        }
    }
}
