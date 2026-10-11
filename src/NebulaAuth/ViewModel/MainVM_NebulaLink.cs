using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.NebulaLink;
using NebulaAuth.ViewModel.NebulaLink;

namespace NebulaAuth.ViewModel;

public partial class MainVM //Nebula Link (Nebula Protocol): status bar indicator, account context menu, row icons
{
    /// <summary>State of every linked account by normalized login (read by the account list icons).</summary>
    [ObservableProperty] private IReadOnlyDictionary<string, string> _linkAccountStates = new Dictionary<string, string>();

    /// <summary>Linked accounts that need attention + failed account links of this session.</summary>
    [ObservableProperty] private int _linkProblems;

    [ObservableProperty] private NebulaLinkState _linkState = NebulaLinkState.Disabled;

    private NebulaLinkUiEvents? _linkEvents;

    public bool IsLinkPaired => LinkState is not (NebulaLinkState.Disabled or NebulaLinkState.Revoked);

    /// <summary>Status bar dot: green = connected.</summary>
    public bool LinkDotOk => LinkState == NebulaLinkState.Connected;

    /// <summary>Status bar dot: grey = connecting or paused by the user.</summary>
    public bool LinkDotIdle => LinkState is NebulaLinkState.Connecting or NebulaLinkState.Paused;

    /// <summary>Status bar dot: orange = a problem of the link itself (no connection, suspended, revoked...).</summary>
    public bool LinkDotProblem => LinkState != NebulaLinkState.Disabled && !LinkDotOk && !LinkDotIdle;

    public bool HasLinkProblems => LinkProblems > 0 && IsLinkPaired;

    public string LinkProblemsBadge => LinkProblems > 99 ? "99+" : LinkProblems.ToString();

    /// <summary>Tooltip of the status bar item: the state and the problems (none before pairing: nothing to add).</summary>
    public string? LinkTooltip
    {
        get
        {
            if (LinkState is NebulaLinkState.Disabled) return null;
            var text = NebulaLinkText.State(LinkState);
            if (HasLinkProblems)
                text += " · " + NebulaLinkText.Plural(LinkProblems, "{0} account needs attention|{0} accounts need attention", "Footer", "Problems");
            return text;
        }
    }

    /// <summary>The account context menu shows "Connect to / Disconnect from Nebula Link" while a link exists.</summary>
    public bool CanConnectSelectedToLink => IsLinkPaired && SelectedMafile != null && !IsSelectedLinked;

    public bool CanDisconnectSelectedFromLink => IsLinkPaired && SelectedMafile != null && IsSelectedLinked;

    private void InitNebulaLink()
    {
        SubscribeNebulaLink();
    }

    /// <summary>The status bar and the account list follow the integration (changes arrive coalesced on the UI thread).</summary>
    private void SubscribeNebulaLink()
    {
        _linkEvents?.Dispose();
        _linkEvents = null;
        if (NebulaLinkHost.Integration is not { } integration) return;
        _linkEvents = new NebulaLinkUiEvents(integration);
        _linkEvents.Changed += _ => RefreshNebulaLink();
        RefreshNebulaLink();
    }

    private void RefreshNebulaLink()
    {
        if (NebulaLinkHost.Integration is not { } integration) return;
        LinkState = integration.State;
        LinkAccountStates = integration.AccountStates();
        LinkProblems = IsLinkPaired ? integration.ProblemsCount() : 0;
        OnPropertyChanged(nameof(IsLinkPaired));
        OnPropertyChanged(nameof(LinkDotOk));
        OnPropertyChanged(nameof(LinkDotIdle));
        OnPropertyChanged(nameof(LinkDotProblem));
        OnPropertyChanged(nameof(HasLinkProblems));
        OnPropertyChanged(nameof(LinkProblemsBadge));
        OnPropertyChanged(nameof(LinkTooltip));
        OnPropertyChanged(nameof(CanConnectSelectedToLink));
        OnPropertyChanged(nameof(CanDisconnectSelectedFromLink));
        NotifyMaacAvailability();
    }

    [RelayCommand]
    private Task OpenNebulaLink()
    {
        return DialogsController.ShowNebulaLinkDialog();
    }

    [RelayCommand]
    private Task ConnectToLink(Mafile? mafile)
    {
        return DialogsController.ShowNebulaLinkDialog(mafile ?? SelectedMafile, true);
    }

    [RelayCommand]
    private Task DisconnectFromLink(Mafile? mafile)
    {
        return DialogsController.ShowNebulaLinkDialog(mafile ?? SelectedMafile, false);
    }
}
