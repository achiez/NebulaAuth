using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.NebulaLink.Pages;

/// <summary>
///     The first page: what Nebula Link is, the pairing link (or code) and the consent. The input is parsed and its
///     origin checked locally before any request; nothing is stored before a successful pairing.
/// </summary>
public partial class ConnectPageVM : ObservableObject
{
    private readonly NebulaLinkVM _owner;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _consent;

    [ObservableProperty] private string? _error;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _input = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isBusy;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _probeDone;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _storeAvailable;

    public ConnectPageVM(NebulaLinkVM owner, string? error = null, bool probe = true)
    {
        _owner = owner;
        _error = error;
        if (probe) _ = ProbeAsync();
    }

    public NebulaLinkVM Owner => _owner;

    /// <summary>The consent checkbox is part of the page until the user accepted it in this session.</summary>
    public bool NeedsConsent => !_owner.Integration.ConsentGiven;

    /// <summary>The secure store check is done and nothing is available: pairing is blocked.</summary>
    public bool NoStore => ProbeDone && !StoreAvailable;

    public bool OriginsLoading => NebulaLinkApis.Status == ApisStatus.Loading && !NebulaLinkApis.CanPair;

    /// <summary>The list of allowed Achiefy addresses could not be fetched: new pairings are not possible.</summary>
    public bool OriginsUnavailable => NebulaLinkApis.Status == ApisStatus.Unavailable;

    public bool CanEnterLink => NebulaLinkApis.CanPair && !IsBusy;

    /// <summary>
    ///     The server the input points to ("example.com"), shown under the field once the input is valid; null
    ///     while the input is empty or invalid.
    /// </summary>
    public string? TargetHost =>
        PairingLink.TryParse(Input, out var target, out _) ? NebulaLinkApis.DisplayHost(target!.Origin) : null;

    public bool HasTargetHost => TargetHost != null && Error == null;
    public bool HasError => Error != null;

    partial void OnInputChanged(string value)
    {
        Error = null;
        OnPropertyChanged(nameof(TargetHost));
        OnPropertyChanged(nameof(HasTargetHost));
    }

    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasTargetHost));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEnterLink));
    }

    partial void OnProbeDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(NoStore));
    }

    partial void OnStoreAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(NoStore));
    }

    private async Task ProbeAsync()
    {
        var store = await _owner.Integration.ProbeCredentialStoreAsync();
        Dispatcher.UIThread.Post(() => SetProbe(store != null));
    }

    internal void SetProbe(bool available)
    {
        StoreAvailable = available;
        ProbeDone = true;
    }

    /// <summary>The allowed origins were fetched (or not) meanwhile.</summary>
    public void RefreshOrigins()
    {
        OnPropertyChanged(nameof(OriginsLoading));
        OnPropertyChanged(nameof(OriginsUnavailable));
        OnPropertyChanged(nameof(CanEnterLink));
        OnPropertyChanged(nameof(TargetHost));
        OnPropertyChanged(nameof(HasTargetHost));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private bool CanConnect()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(Input) && (Consent || !NeedsConsent) && ProbeDone &&
               StoreAvailable && NebulaLinkApis.CanPair;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        Error = null;
        if (!PairingLink.TryParse(Input, out var target, out var parseError))
        {
            Error = ErrorText(parseError);
            return;
        }

        IsBusy = true;
        try
        {
            if (NeedsConsent) _owner.Integration.Enable();
            var pairing = await _owner.Integration.RedeemAsync(target!, CancellationToken.None);
            _owner.Show(new VerifyPageVM(_owner, pairing));
        }
        catch (NebulaApiException ex)
        {
            Error = ErrorText(ex.Code);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Pairing failed");
            Error = ErrorText(null);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(NeedsConsent));
        }
    }

    [RelayCommand]
    private async Task Paste()
    {
        var text = await ClipboardHelper.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) Input = text.Trim();
    }

    [RelayCommand]
    private static Task RetryOrigins()
    {
        return LinksManager.FetchAsync();
    }

    public static string ErrorText(string? code)
    {
        return code switch
        {
            PairingLink.ERROR_EMPTY or PairingLink.ERROR_LINK_INVALID => NebulaLinkText.Get(
                "Not a Nebula Link link. Copy the whole link from Achiefy.", "PairingError", "LinkInvalid"),
            PairingLink.ERROR_ORIGIN_NOT_ALLOWED => NebulaLinkText.Get(
                "Unknown server. Only official Achiefy addresses are allowed.",
                "PairingError", "OriginNotAllowed"),
            PairingLink.ERROR_NO_DEFAULT_ORIGIN => NebulaLinkText.Get(
                "Paste the whole pairing link, not only the code.", "PairingError", "NoDefaultOrigin"),
            NebulaApiException.CODE_PAIRING_CODE_INVALID => NebulaLinkText.Get(
                "The link is invalid or used. Create a new one.", "PairingError", "CodeInvalid"),
            NebulaResultCodes.RateLimited => NebulaLinkText.Get("Too many attempts. Try again in a few minutes.",
                "PairingError", "RateLimited"),
            NebulaApiException.CODE_REDIRECT => NebulaLinkText.Get(
                "Unexpected server response. Pairing stopped.",
                "PairingError", "Redirect"),
            NebulaApiException.CODE_ORIGIN_MISMATCH => NebulaLinkText.Get(
                "The server reported another address. Pairing stopped.", "PairingError", "OriginMismatch"),
            NebulaApiException.CODE_PROTOCOL_INCOMPATIBLE => NebulaLinkText.Get(
                "Update NebulaAuth.", "PairingError", "Incompatible"),
            NebulaApiException.CODE_NETWORK => NebulaLinkText.Get(
                "Achiefy is not reachable. Check the internet.", "PairingError", "Network"),
            _ => NebulaLinkText.Get("Couldn't connect. Try again later.", "PairingError", "Unknown")
        };
    }
}

/// <summary>The exact verified address, the Achiefy account and the workspace before anything is stored.</summary>
public partial class VerifyPageVM : ObservableObject
{
    private readonly NebulaLinkVM _owner;

    public VerifyPageVM(NebulaLinkVM owner, PendingPairing pairing)
    {
        _owner = owner;
        Pairing = pairing;
    }

    public NebulaLinkVM Owner => _owner;
    public PendingPairing Pairing { get; }
    public string Origin => Pairing.Origin;
    public bool IsSecure => Pairing.Origin.StartsWith("https://", StringComparison.Ordinal);

    [RelayCommand]
    private void Continue()
    {
        _owner.Show(new WaitingPageVM(_owner, Pairing));
    }

    [RelayCommand]
    private async Task Cancel()
    {
        await _owner.Integration.CancelPairingAsync();
        _owner.Show(new ConnectPageVM(_owner));
    }
}

/// <summary>Waiting for the approval in Achiefy: claim polling, countdown, animation (reduced motion aware).</summary>
public partial class WaitingPageVM : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly NebulaLinkVM _owner;
    private readonly DispatcherTimer? _timer;
    [ObservableProperty] private string _remaining = "";

    public WaitingPageVM(NebulaLinkVM owner, PendingPairing pairing, bool start = true)
    {
        _owner = owner;
        Pairing = pairing;
        UpdateRemaining();
        if (!start) return;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRemaining());
        _timer.Start();
        _ = WaitAsync();
    }

    public NebulaLinkVM Owner => _owner;
    public PendingPairing Pairing { get; }
    public string Host => NebulaLinkApis.DisplayHost(Pairing.Origin);
    public bool Animate => !NebulaLinkVM.ReduceMotion;

    private void UpdateRemaining()
    {
        var left = Pairing.ApprovalExpiresAt - DateTimeOffset.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        Remaining = left.ToString(@"m\:ss");
    }

    private async Task WaitAsync()
    {
        PairingWaitResult result;
        try
        {
            result = await _owner.Integration.WaitForApprovalAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Pairing approval wait failed");
            result = PairingWaitResult.Failed;
        }

        if (_cts.IsCancellationRequested) return;
        if (result == PairingWaitResult.Approved)
        {
            _owner.Show(new SuccessPageVM(_owner));
            return;
        }

        _owner.Show(new ConnectPageVM(_owner, ErrorText(result)));
    }

    public static string ErrorText(PairingWaitResult result)
    {
        return result switch
        {
            PairingWaitResult.Rejected => NebulaLinkText.Get("The connection was rejected in Achiefy.", "PairingError", "Rejected"),
            PairingWaitResult.Expired => NebulaLinkText.Get("Time is up. Create a new link.", "PairingError", "Expired"),
            PairingWaitResult.Cancelled => NebulaLinkText.Get("Cancelled in Achiefy.", "PairingError", "Cancelled"),
            PairingWaitResult.StoreFailed => NebulaLinkText.Get(
                "Couldn't save the key. Turn on encryption or check the keyring.",
                "PairingError", "StoreFailed"),
            _ => NebulaLinkText.Get("Couldn't connect. Try again later.", "PairingError", "Unknown")
        };
    }

    [RelayCommand]
    private async Task Cancel()
    {
        _cts.Cancel();
        await _owner.Integration.CancelPairingAsync();
        _owner.Show(new ConnectPageVM(_owner));
    }

    public void Dispose()
    {
        _timer?.Stop();
        _cts.Cancel();
    }
}

/// <summary>Short success animation, then the accounts table.</summary>
public partial class SuccessPageVM : ObservableObject, IDisposable
{
    private readonly DispatcherTimer? _timer;

    public SuccessPageVM(NebulaLinkVM owner, bool autoAdvance = true)
    {
        Owner = owner;
        if (!autoAdvance) return;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1400), DispatcherPriority.Normal, (_, _) =>
        {
            _timer!.Stop();
            owner.Show(new ManagePageVM(owner));
        });
        _timer.Start();
    }

    public NebulaLinkVM Owner { get; }
    public bool Animate => !NebulaLinkVM.ReduceMotion;

    public void Dispose()
    {
        _timer?.Stop();
    }
}

/// <summary>Revoked, replaced, incompatible, no credential, credential refused.</summary>
public partial class StatePageVM : ObservableObject
{
    private readonly NebulaLinkVM _owner;
    [ObservableProperty] private bool _isBusy;

    public StatePageVM(NebulaLinkVM owner, NebulaLinkState kind)
    {
        _owner = owner;
        Kind = kind;
    }

    public NebulaLinkVM Owner => _owner;
    public NebulaLinkState Kind { get; }

    public string Title => NebulaLinkText.Get(Kind.ToString(), "StatePage", Kind + "Title");

    public string Text => Kind switch
    {
        NebulaLinkState.Revoked => RevokedReason() ?? NebulaLinkText.Get(
            "The access key was deleted from this computer.",
            "StatePage", "RevokedText"),
        NebulaLinkState.Replaced => NebulaLinkText.Get(
            "Only one copy can be connected.",
            "StatePage", "ReplacedText"),
        NebulaLinkState.Incompatible => _owner.Integration.ServerNewer
            ? NebulaLinkText.Get("Update NebulaAuth.", "StatePage",
                "IncompatibleNewer")
            : NebulaLinkText.Get("Achiefy doesn't support this version yet. Retrying every 6 hours.",
                "StatePage", "IncompatibleOlder"),
        NebulaLinkState.CredentialUnavailable => NebulaLinkText.Get(
            "Secure storage is unavailable. Turn encryption back on or unlink.",
            "StatePage", "CredentialUnavailableText"),
        NebulaLinkState.CredentialRejected => NebulaLinkText.Get(
            "The link was probably revoked while NebulaAuth was offline.",
            "StatePage", "CredentialRejectedText"),
        _ => ""
    };

    /// <summary>Icon of the page: link-off (revoked / replaced), update (incompatible), key (credential problems).</summary>
    public bool IsKeyProblem => Kind is NebulaLinkState.CredentialUnavailable or NebulaLinkState.CredentialRejected;

    public bool IsUpdate => Kind is NebulaLinkState.Incompatible;
    public bool IsLinkOff => !IsKeyProblem && !IsUpdate;

    public bool CanPairAgain => Kind is NebulaLinkState.Revoked;

    public bool CanReconnect => Kind is NebulaLinkState.Replaced or NebulaLinkState.CredentialRejected
        or NebulaLinkState.Incompatible;

    public bool CanUnlink => Kind is NebulaLinkState.Replaced or NebulaLinkState.CredentialUnavailable
        or NebulaLinkState.CredentialRejected or NebulaLinkState.Incompatible;

    public string ReconnectText => Kind == NebulaLinkState.Replaced
        ? NebulaLinkText.Get("Use here", "StatePage", "ReconnectHere")
        : NebulaLinkText.Get("Try again", "StatePage", "TryAgain");

    /// <summary>Why the server revoked the link, as a sentence; null when it gave no reason.</summary>
    private string? RevokedReason()
    {
        var reason = _owner.Integration.StateReason;
        return string.IsNullOrEmpty(reason) ? null : NebulaLinkText.Get(reason, "Reason", reason);
    }

    [RelayCommand]
    private void PairAgain()
    {
        _owner.Integration.AcknowledgeRevoked();
        _owner.Show(new ConnectPageVM(_owner));
    }

    [RelayCommand]
    private async Task Reconnect()
    {
        IsBusy = true;
        try
        {
            await _owner.Integration.ReconnectAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Unlink()
    {
        IsBusy = true;
        try
        {
            await _owner.Integration.UnlinkAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
