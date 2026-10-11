using System;
using System.Linq;
using NebulaAuth.Core;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.ViewModel.NebulaLink;
using NebulaAuth.ViewModel.NebulaLink.Demo;
using NebulaAuth.ViewModel.NebulaLink.Pages;

namespace NebulaAuth.View;

public partial class NebulaLinkView
{
    /// <summary>
    ///     Previews (NEBULA_PREVIEW=NebulaAuth.View.NebulaLinkView, NEBULA_PREVIEW_ARG=...):
    ///     pairing: pair, pair-filled, pair-code, pair-error, pair-origin, pair-network, pair-expired, pair-unavailable,
    ///     pair-nostore, verify, waiting, success;
    ///     connected: accounts, accounts-narrow, details, details-narrow, errors, duplicates, duplicates-narrow, history,
    ///     menu, ov-connect,
    ///     ov-disconnect, ov-permissions, ov-unlink, ov-password, ov-error, paused, offline, suspended, revoked-inactive;
    ///     states: revoked, replaced, incompatible, incompatible-older, unavailable, rejected.
    ///     Fake in-memory link with 2000 generated rows and a request history; nothing is stored, nothing goes to the
    ///     network. Wide pages enlarge the main window first (the modal is constrained by the window).
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        arg = (arg ?? "accounts").Trim().ToLowerInvariant();
        var narrow = arg.EndsWith("-narrow", StringComparison.Ordinal);
        var compact = arg.StartsWith("pair", StringComparison.Ordinal) || arg is "verify" or "waiting" or "success" or
            "revoked" or "revoked-inactive" or "replaced" or "incompatible" or "incompatible-older" or "unavailable" or "rejected";
        var window = AppHost.MainWindow;
        if (window != null && !narrow && !compact)
        {
            window.Width = 1180;
            window.Height = 780;
        }

        FakeNebulaLink.SetOrigins(arg != "pair-unavailable");
        var state = arg switch
        {
            "pair" or "pair-filled" or "pair-code" or "pair-error" or "pair-origin" or "pair-unavailable"
                or "pair-nostore" or "pair-network" or "pair-expired" or "verify" or "waiting" or "success" =>
                NebulaLinkState.Disabled,
            "paused" => NebulaLinkState.Paused,
            "offline" => NebulaLinkState.Offline,
            "suspended" => NebulaLinkState.Suspended,
            "revoked" or "revoked-inactive" => NebulaLinkState.Revoked,
            "replaced" => NebulaLinkState.Replaced,
            "incompatible" or "incompatible-older" => NebulaLinkState.Incompatible,
            "unavailable" => NebulaLinkState.CredentialUnavailable,
            "rejected" => NebulaLinkState.CredentialRejected,
            _ => NebulaLinkState.Connected
        };
        var integration = FakeNebulaLink.Create(state, storeAvailable: arg != "pair-nostore");
        if (arg == "revoked-inactive") integration.PreviewSetStateReason(NebulaLinkReasons.Inactive);
        if (arg == "incompatible-older") integration.PreviewSetServerNewer(false);
        // A failed pairing of this session: the consent was given before
        if (arg is "pair-network" or "pair-expired") integration.Enable();
        var vm = new NebulaLinkVM(integration);
        var pairing = new PendingPairing
        {
            PairingId = Guid.NewGuid(), Origin = "https://example.com", TenantBaseUrl = "https://example.com/api",
            ApiOrigin = "https://example.com", TenantId = 1, TenantLabel = "ID 1",
            AccountLabel = "trader@example.com", ApprovalExpiresAt = DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(41),
            PollIntervalMs = 2000, OriginMatches = true
        };
        const string link = "https://example.com/api/nebula/pair#7KQ2M-X9D4R";
        switch (arg)
        {
            case "pair" or "pair-unavailable" or "pair-nostore":
                vm.Show(Connect(vm, arg != "pair-nostore"));
                break;
            case "pair-filled":
                vm.Show(Connect(vm, true, link, consent: true));
                break;
            case "pair-code":
                vm.Show(Connect(vm, true, "7KQ2M-X9D4R", consent: true));
                break;
            case "pair-error":
                vm.Show(Connect(vm, true, "https://example.com/api/nebula/pair#7KQ2M",
                    ConnectPageVM.ErrorText(NebulaApiException.CODE_PAIRING_CODE_INVALID), true));
                break;
            case "pair-origin":
                vm.Show(Connect(vm, true, "https://phishing.example/api/nebula/pair#7KQ2M-X9D4R",
                    ConnectPageVM.ErrorText(PairingLink.ERROR_ORIGIN_NOT_ALLOWED), true));
                break;
            case "pair-network":
                vm.Show(Connect(vm, true, link, ConnectPageVM.ErrorText(NebulaApiException.CODE_NETWORK)));
                break;
            case "pair-expired":
                vm.Show(Connect(vm, true, "", WaitingPageVM.ErrorText(PairingWaitResult.Expired)));
                break;
            case "verify":
                vm.Show(new VerifyPageVM(vm, pairing));
                break;
            case "waiting":
                vm.Show(new WaitingPageVM(vm, pairing, false));
                break;
            case "success":
                vm.Show(new SuccessPageVM(vm, false));
                break;
        }

        if (vm.CurrentPage is ManagePageVM manage)
        {
            switch (arg.Replace("-narrow", ""))
            {
                case "details":
                    manage.SelectedFilter = manage.Filters[0];
                    manage.SelectedRow = manage.VisibleRows.FirstOrDefault(r => r.IsLinked && r.Link!.Grants.Count > 1) ??
                                         manage.VisibleRows.FirstOrDefault();
                    manage.NarrowDetailsOpen = true;
                    break;
                case "result":
                    // The notice after a bulk connect with a skipped account
                    manage.BulkResult = NebulaLinkText.Format("Connected: {0}", ["Manage", "ConnectedCount"], 2) + " · " +
                                        NebulaLinkText.Format("Skipped: {0}", ["Manage", "SkippedCount"], 1);
                    manage.BulkResultHint = "alkaira_7: " + NebulaLinkText.LinkResult(NebulaAccountLinkResults.LoginMismatch);
                    break;
                case "accounts-none":
                    // An account without an authenticator: it switches to Nebula Link when connected
                    manage.SelectedRow = manage.VisibleRows.FirstOrDefault(r =>
                        r.IsLinkable && r.Server?.Source == NebulaAuthenticatorSources.None);
                    break;
                case "accounts":
                    manage.SelectedRow = manage.VisibleRows.FirstOrDefault(r => r.IsLinkable);
                    foreach (var row in manage.VisibleRows.Where(r => r.IsLinkable).Take(2)) row.IsChecked = true;
                    foreach (var row in manage.VisibleRows.Where(r => r.IsLinked).Take(1)) row.IsChecked = true;
                    break;
                case "errors":
                    manage.SelectedFilter = manage.Filters.First(f => f.Key == "error");
                    manage.SelectedRow = manage.VisibleRows.FirstOrDefault(r => r.IsDuplicate) ??
                                         manage.VisibleRows.FirstOrDefault();
                    break;
                case "history":
                    manage.SelectedTab = 1;
                    break;
                case "duplicates":
                    manage.SelectedFilter = manage.Filters[0];
                    manage.SearchText = "trader_dup";
                    manage.SelectedRow = manage.VisibleRows.FirstOrDefault(r => r.IsDuplicate);
                    manage.NarrowDetailsOpen = true;
                    break;
                case "ov-connect":
                    foreach (var row in manage.VisibleRows.Where(r => r.IsLinkable).Take(3)) row.IsChecked = true;
                    manage.ConnectSelectedCommand.Execute(null);
                    PermissionGroupVM.Optional(manage.ConnectGrants).First().IsOn = true;
                    break;
                case "ov-disconnect":
                    foreach (var row in manage.VisibleRows.Where(r => r.IsLinked).Take(2)) row.IsChecked = true;
                    manage.DisconnectSelectedCommand.Execute(null);
                    break;
                case "ov-permissions":
                    foreach (var row in manage.VisibleRows.Where(r => r.IsLinked).Take(4)) row.IsChecked = true;
                    manage.ChangePermissionsCommand.Execute(null);
                    break;
                case "ov-unlink":
                    manage.UnlinkDeviceCommand.Execute(null);
                    break;
                case "ov-password":
                    manage.SelectedFilter = manage.Filters[0];
                    manage.SelectedRow = manage.VisibleRows.FirstOrDefault(r => r.InvalidPassword);
                    manage.UpdatePasswordCommand.Execute(null);
                    break;
                case "ov-error":
                    foreach (var row in manage.VisibleRows.Where(r => r.IsLinkable).Take(2)) row.IsChecked = true;
                    manage.ConnectSelectedCommand.Execute(null);
                    manage.OverlayError = NebulaLinkText.Result(NebulaResultCodes.DeviceOffline);
                    break;
            }
        }

        var view = new NebulaLinkView {DataContext = vm};
        if (arg == "menu")
            Avalonia.Threading.DispatcherTimer.RunOnce(() => view.MenuButton.Flyout?.ShowAt(view.MenuButton),
                TimeSpan.FromSeconds(1.5));
        return view;
    }

    private static ConnectPageVM Connect(NebulaLinkVM owner, bool store, string input = "", string? error = null,
        bool consent = false)
    {
        var page = new ConnectPageVM(owner, null, false) {Input = input, Consent = consent};
        page.SetProbe(store);
        page.Error = error;
        return page;
    }
}
