using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.NebulaLink;
using NebulaAuth.Utility;
using NebulaAuth.ViewModel.NebulaLink.Pages;

namespace NebulaAuth.ViewModel.NebulaLink;

/// <summary>
///     The single Nebula Link modal: a compact vertical card for pairing and state pages (connect → verify → waiting →
///     success), a wide one for the connected link (accounts table, request history). No nested dialogs: confirmations
///     are inline overlays of the pages.
/// </summary>
public partial class NebulaLinkVM : ObservableObject, IDisposable
{
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private bool _isBusy;
    private readonly NebulaLinkUiEvents _events;
    private bool _disposed;
    private double _modalWidth = 1040;

    public NebulaLinkVM(NebulaLinkIntegration integration, Mafile? focus = null, bool? linkIntent = null)
    {
        Integration = integration;
        Focus = focus;
        LinkIntent = linkIntent;
        _events = new NebulaLinkUiEvents(integration);
        _events.Changed += OnIntegrationChanged;
        NebulaLinkApis.Changed += OnOriginsChanged;
        Navigate(true);
    }

    public NebulaLinkIntegration Integration { get; }

    /// <summary>Account the modal was opened for (account context menu).</summary>
    public Mafile? Focus { get; }

    /// <summary>The context menu action: true = connect, false = disconnect.</summary>
    public bool? LinkIntent { get; }

    public static bool ReduceMotion => Settings.Instance.ReduceMotion;

    /// <summary>Modal width in device-independent pixels (set by the view): the accounts page adapts below 860.</summary>
    public double ModalWidth
    {
        get => _modalWidth;
        set
        {
            if (!SetProperty(ref _modalWidth, value)) return;
            if (CurrentPage is ManagePageVM manage) manage.IsNarrow = value < 860;
        }
    }

    /// <summary>The connected link: the wide modal with the accounts table.</summary>
    public bool IsManage => CurrentPage is ManagePageVM;

    /// <summary>Who and where: "trader@example.com · example.com".</summary>
    public string AccountLine
    {
        get
        {
            var data = Integration.Links.Snapshot();
            var host = Integration.LinkOrigin is { } origin ? NebulaLinkApis.DisplayHost(origin) : null;
            var account = string.IsNullOrWhiteSpace(data?.AccountLabel) ? data?.TenantLabel : data.AccountLabel;
            return string.Join(" · ", new[] {account, host}.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public NebulaLinkState State => Integration.State;
    public string StateText => NebulaLinkText.State(Integration.State);

    /// <summary>Chip color: Ok (connected), Warn (connecting, offline, paused), Bad (suspended and worse).</summary>
    public bool StateOk => State == NebulaLinkState.Connected;

    public bool StateWarn => State is NebulaLinkState.Connecting or NebulaLinkState.Offline or NebulaLinkState.Paused;
    public bool StateBad => !StateOk && !StateWarn;

    public bool CanPause => IsManage && State is NebulaLinkState.Connected or NebulaLinkState.Connecting
        or NebulaLinkState.Offline or NebulaLinkState.Suspended;

    public bool CanResume => IsManage && State == NebulaLinkState.Paused;

    private void OnIntegrationChanged(NebulaLinkUiChange change)
    {
        if (_disposed) return;
        if (change.State) Navigate(false);
        (CurrentPage as ManagePageVM)?.OnIntegrationChanged(change);
        RaiseHeader();
    }

    private void OnOriginsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            (CurrentPage as ConnectPageVM)?.RefreshOrigins();
            OpenCabinetCommand.NotifyCanExecuteChanged();
            OpenDataDescriptionCommand.NotifyCanExecuteChanged();
        });
    }

    /// <summary>Picks the page for the current state (pairing pages are kept while a pairing is in progress).</summary>
    public void Navigate(bool initial)
    {
        var state = Integration.State;
        switch (state)
        {
            case NebulaLinkState.Disabled:
                if (CurrentPage is ConnectPageVM or VerifyPageVM or WaitingPageVM or SuccessPageVM) break;
                // Reopened while a redeemed pairing still waits for the approval: keep waiting
                if (initial && Integration.Pairing is { } pending && pending.ApprovalExpiresAt > DateTimeOffset.UtcNow)
                    Show(new WaitingPageVM(this, pending));
                else Show(new ConnectPageVM(this));
                break;
            case NebulaLinkState.Connecting or NebulaLinkState.Connected or NebulaLinkState.Offline
                or NebulaLinkState.Paused or NebulaLinkState.Suspended:
                // The waiting page shows the success animation itself
                if (CurrentPage is SuccessPageVM or WaitingPageVM) break;
                if (CurrentPage is not ManagePageVM) Show(new ManagePageVM(this));
                break;
            default:
                if (CurrentPage is StatePageVM sp && sp.Kind == state) break;
                Show(new StatePageVM(this, state));
                break;
        }
    }

    public void Show(object page)
    {
        (CurrentPage as IDisposable)?.Dispose();
        if (page is ManagePageVM manage) manage.IsNarrow = ModalWidth < 860;
        CurrentPage = page;
        RaiseHeader();
    }

    partial void OnCurrentPageChanged(object? value)
    {
        LayoutChanged?.Invoke();
    }

    /// <summary>The modal switches between the compact and the wide layout (the view resizes itself).</summary>
    public event Action? LayoutChanged;

    private void RaiseHeader()
    {
        OnPropertyChanged(nameof(IsManage));
        OnPropertyChanged(nameof(AccountLine));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateOk));
        OnPropertyChanged(nameof(StateWarn));
        OnPropertyChanged(nameof(StateBad));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    [RelayCommand]
    private async Task Pause()
    {
        IsBusy = true;
        try
        {
            await Integration.PauseAsync();
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Nebula Link pause failed");
        }
        finally
        {
            IsBusy = false;
            RaiseHeader();
        }
    }

    [RelayCommand]
    private async Task Resume()
    {
        IsBusy = true;
        try
        {
            await Integration.ResumeAsync();
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Nebula Link resume failed");
        }
        finally
        {
            IsBusy = false;
            RaiseHeader();
        }
    }

    /// <summary>Header menu "Unlink this computer": the confirmation is an overlay of the accounts page.</summary>
    [RelayCommand]
    private void RequestUnlink()
    {
        if (CurrentPage is ManagePageVM manage) manage.UnlinkDeviceCommand.Execute(null);
    }

    // The Achiefy pages come from the published links.json (opened in the browser, never fetched)
    private bool CanOpenCabinet() => LinksManager.NebulaLinkCabinetUrl != null;
    private bool CanOpenDataDescription() => LinksManager.NebulaLinkDataUrl != null;

    [RelayCommand(CanExecute = nameof(CanOpenCabinet))]
    private void OpenCabinet()
    {
        PlatformUtils.OpenUrl(LinksManager.NebulaLinkCabinetUrl!);
    }

    [RelayCommand(CanExecute = nameof(CanOpenDataDescription))]
    private void OpenDataDescription()
    {
        PlatformUtils.OpenUrl(LinksManager.NebulaLinkDataUrl!);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events.Dispose();
        NebulaLinkApis.Changed -= OnOriginsChanged;
        (CurrentPage as IDisposable)?.Dispose();
    }
}
