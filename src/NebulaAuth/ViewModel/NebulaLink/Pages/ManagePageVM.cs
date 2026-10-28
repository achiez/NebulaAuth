using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.Utility;
using SteamLibForked.Exceptions.Authorization;

namespace NebulaAuth.ViewModel.NebulaLink.Pages;

public enum ManageOverlay
{
    None,
    Connect,
    Disconnect,
    Permissions,
    UnlinkDevice,
    Password
}

/// <summary>
///     After pairing: the accounts table (search, filters, counts, selection, bulk actions, side details with grants) and
///     the request history. Confirmations are inline overlays (no nested dialogs).
/// </summary>
public partial class ManagePageVM : ObservableObject
{
    public const string FILTER_ALL = "all";
    private readonly NebulaLinkVM _owner;
    private List<AccountRowVM> _all = [];

    [ObservableProperty] private string? _bulkResult;

    /// <summary>Why accounts were skipped by the last bulk change ("login: reason" per line); null when none were.</summary>
    [ObservableProperty] private string? _bulkResultHint;

    /// <summary>Some accounts of the disconnect overlay use Nebula Link as their source: they are left without one.</summary>
    [ObservableProperty] private bool _disconnectHasNebula;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isNarrow;
    [ObservableProperty] private bool _narrowDetailsOpen;
    [ObservableProperty] private ManageOverlay _overlay;
    [ObservableProperty] private string? _overlayError;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _savePassword;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FilterOption _selectedFilter;
    [ObservableProperty] private AccountRowVM? _selectedRow;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private IReadOnlyList<AccountRowVM> _visibleRows = [];
    [ObservableProperty] private IReadOnlyList<HistoryRowVM> _historyRows = [];

    public ManagePageVM(NebulaLinkVM owner)
    {
        _owner = owner;
        Filters =
        [
            new FilterOption(FILTER_ALL, NebulaLinkText.Get("All", "Filter", "All")),
            new FilterOption("inAchiefy", NebulaLinkText.Get("In Achiefy", "Filter", "InAchiefy")),
            new FilterOption("linked", NebulaLinkText.Get("Connected", "Filter", "Connected")),
            new FilterOption("linkable", NebulaLinkText.Get("Can be connected", "Filter", "Linkable")),
            new FilterOption("needsLogin", NebulaLinkText.Get("Login required", "Filter", "NeedsLogin")),
            new FilterOption("error", NebulaLinkText.Get("Errors", "Filter", "Error")),
            new FilterOption("onlyLocal", NebulaLinkText.Get("Only in NebulaAuth", "Filter", "OnlyLocal")),
            new FilterOption("onlyAchiefy", NebulaLinkText.Get("Only in Achiefy", "Filter", "OnlyAchiefy"))
        ];
        _selectedFilter = Filters[1];
        Rebuild();
        RebuildHistory();
        ApplyFocus();
    }

    public NebulaLinkVM Owner => _owner;
    private NebulaLinkIntegration Integration => _owner.Integration;
    public IReadOnlyList<FilterOption> Filters { get; }

    /// <summary>Permissions chosen for new links: the mandatory ones (checked, disabled), the catalog modules off.</summary>
    [ObservableProperty] private IReadOnlyList<PermissionGroupVM> _connectGrants = [];

    /// <summary>Permissions of the checked connected accounts (bulk change).</summary>
    [ObservableProperty] private IReadOnlyList<PermissionGroupVM> _permissionGrants = [];

    /// <summary>The server has not sent its module catalog yet: only the mandatory permissions are shown.</summary>
    public bool NoCatalog => Integration.ModuleCatalog.Modules.Length == 0;

    private IReadOnlyList<PermissionGroupVM> BuildOverlayGrants(Func<GrantKey, bool> isOn)
    {
        return PermissionGroupVM.Build(Integration.ModuleCatalog, true, true, isOn, null);
    }

    private static IEnumerable<GrantVM> Optional(IEnumerable<PermissionGroupVM> groups)
    {
        return PermissionGroupVM.Optional(groups);
    }

    public bool IsOnline => Integration.State is NebulaLinkState.Connected;
    public bool ShowOfflineHint => !IsOnline;

    public string OfflineHint => Integration.State == NebulaLinkState.Paused
        ? NebulaLinkText.Get("Achiefy requests are refused.", "Manage", "PausedHint")
        : NebulaLinkText.Get("No connection: accounts can't be changed.", "Manage", "OfflineHint");

    public bool AccountsNotLoaded => !Integration.ServerAccountsLoaded;

    #region Counts and selection

    public int CheckedCount => _all.Count(r => r.IsChecked);
    public int CheckedLinkable => _all.Count(r => r.IsChecked && r.IsLinkable);
    public int CheckedLinked => _all.Count(r => r.IsChecked && r.IsLinked);
    public string CountsText => NebulaLinkText.Format("{0} of {1} connected", ["Manage", "Counts"],
        _all.Count(r => r.IsLinked), _all.Count(r => r.Server != null));

    public string SelectedText => NebulaLinkText.Format("Selected: {0}", ["Manage", "Selected"], CheckedCount);

    public string ConnectSelectedText =>
        NebulaLinkText.Format("Connect ({0})", ["Manage", "ConnectSelected"], CheckedLinkable);

    public string DisconnectSelectedText =>
        NebulaLinkText.Format("Disconnect ({0})", ["Manage", "DisconnectSelected"], CheckedLinked);

    public string PermissionsText =>
        NebulaLinkText.Format("Permissions ({0})", ["Manage", "ChangePermissions"], CheckedLinked);

    /// <summary>
    ///     Header checkbox: true when every visible row is checked, null when some are. Setting it checks or unchecks
    ///     every visible row.
    /// </summary>
    public bool? AllVisibleChecked
    {
        get
        {
            var visible = VisibleRows;
            if (visible.Count == 0) return false;
            var checkedCount = visible.Count(r => r.IsChecked);
            return checkedCount == 0 ? false : checkedCount == visible.Count ? true : null;
        }
        set
        {
            // A click on an indeterminate box checks everything (Avalonia cycles false → true → null otherwise)
            var check = value != false || AllVisibleChecked == null;
            if (AllVisibleChecked == true) check = false;
            _bulkChecking = true;
            try
            {
                foreach (var row in VisibleRows) row.IsChecked = check;
            }
            finally
            {
                _bulkChecking = false;
            }

            RaiseSelection();
        }
    }

    private bool _bulkChecking;

    public string ConnectOverlayTitle =>
        NebulaLinkText.Plural(CheckedLinkable, "Connect {0} account|Connect {0} accounts", "Manage", "ConnectTitle");

    public string DisconnectOverlayTitle =>
        NebulaLinkText.Plural(CheckedLinked, "Disconnect {0} account|Disconnect {0} accounts", "Manage", "DisconnectTitle");

    public string PermissionsOverlayTitle =>
        NebulaLinkText.Plural(CheckedLinked, "Permissions of {0} account|Permissions of {0} accounts", "Manage", "PermissionsTitle");

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckedLinkable));
        OnPropertyChanged(nameof(CheckedLinked));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(AllVisibleChecked));
        OnPropertyChanged(nameof(ConnectSelectedText));
        OnPropertyChanged(nameof(DisconnectSelectedText));
        OnPropertyChanged(nameof(PermissionsText));
        OnPropertyChanged(nameof(ConnectOverlayTitle));
        OnPropertyChanged(nameof(DisconnectOverlayTitle));
        OnPropertyChanged(nameof(PermissionsOverlayTitle));
        ConnectSelectedCommand.NotifyCanExecuteChanged();
        DisconnectSelectedCommand.NotifyCanExecuteChanged();
        ChangePermissionsCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Rows

    /// <summary>Coalesced changes (UI thread): the rows are rebuilt once, the history only when it changed.</summary>
    internal void OnIntegrationChanged(NebulaLinkUiChange change)
    {
        if (change.History) RebuildHistory();
        // The "Last request" column follows the history
        Rebuild();
        if (!change.State && !change.Accounts) return;
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(ShowOfflineHint));
        OnPropertyChanged(nameof(OfflineHint));
        OnPropertyChanged(nameof(AccountsNotLoaded));
        OnPropertyChanged(nameof(NoCatalog));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ConnectSelectedCommand.NotifyCanExecuteChanged();
        DisconnectSelectedCommand.NotifyCanExecuteChanged();
        ChangePermissionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rebuilds the rows from the integration (selection and checks are kept).</summary>
    public void Rebuild()
    {
        var existing = _all.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var selected = SelectedRow;
        var links = Integration.Links.Accounts();
        var linkByLogin = links.GroupBy(l => l.Login).ToDictionary(g => g.Key, g => g.First());
        var linkByAccount = links.GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.First());
        var index = Integration.ResolveLocal();
        var catalog = Integration.ModuleCatalog;
        var last = Integration.History.LastRequestByAccount();
        var rows = new List<AccountRowVM>();
        var shownLinks = new HashSet<int>();
        foreach (var match in Integration.MatchRows())
        {
            linkByLogin.TryGetValue(match.Login, out var link);
            if (link == null && match.Server != null) linkByAccount.TryGetValue(match.Server.AccountId, out link);
            if (link != null) shownLinks.Add(link.AccountId);
            var state = link != null ? Integration.ComputeAccountState(link, index) : null;
            var accountId = match.Server?.AccountId ?? link?.AccountId;
            var failure = accountId != null ? Integration.LinkFailure(accountId.Value) : null;
            var lastRequest = accountId != null ? last.GetValueOrDefault(accountId.Value) : null;
            var probe = new AccountRowVM(match, link, state, failure, lastRequest) {Catalog = catalog};
            if (existing.Remove(probe.Key, out var row))
            {
                var catalogChanged = !ReferenceEquals(row.Catalog, catalog);
                row.Catalog = catalog;
                if (catalogChanged && ReferenceEquals(row, selected)) row.BuildGrants(OnGrantToggled);
                if (row.Update(match, link, state, failure, lastRequest) && ReferenceEquals(row, selected))
                    row.BuildGrants(OnGrantToggled);
            }
            else
            {
                row = probe;
                row.PropertyChanged += RowOnPropertyChanged;
            }

            rows.Add(row);
        }

        foreach (var gone in existing.Values) gone.PropertyChanged -= RowOnPropertyChanged;
        _all = rows;
        ApplyFilter();
        if (selected != null && !_all.Contains(selected)) SelectedRow = _all.FirstOrDefault(r => r.Key == selected.Key);
        OnPropertyChanged(nameof(CountsText));
        RaiseSelection();
    }

    private void RowOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountRowVM.IsChecked) && !_bulkChecking) RaiseSelection();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedFilterChanged(FilterOption value)
    {
        ApplyFilter();
    }

    partial void OnSelectedRowChanged(AccountRowVM? value)
    {
        value?.BuildGrants(OnGrantToggled);
        OnPropertyChanged(nameof(HasSelectedRow));
    }

    public bool HasSelectedRow => SelectedRow != null;

    private void ApplyFilter()
    {
        var search = SearchText.Trim().ToLowerInvariant();
        var key = SelectedFilter?.Key ?? FILTER_ALL;
        var visible = _all.Where(r => (search.Length == 0 || r.SearchText.Contains(search, StringComparison.Ordinal)) &&
                                      key switch
                                      {
                                          "inAchiefy" => r.InAchiefy,
                                          "linked" => r.IsLinked,
                                          "linkable" => r.IsLinkable,
                                          "needsLogin" => r.NeedsLogin,
                                          "error" => r.IsError,
                                          "onlyLocal" => r.Row.Kind == MatchKind.OnlyLocal,
                                          "onlyAchiefy" => r.Row.Kind == MatchKind.OnlyAchiefy,
                                          _ => true
                                      })
            .OrderBy(r => r.Local == null)
            .ThenBy(r => r.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The same rows in the same order: keep the list (scroll position) as it is
        if (!visible.SequenceEqual(VisibleRows)) VisibleRows = visible;
        RaiseSelection();
    }

    private void ApplyFocus()
    {
        var focus = _owner.Focus;
        if (focus == null) return;
        var row = _all.FirstOrDefault(r => r.Local == focus) ??
                  _all.FirstOrDefault(r => r.Row.Login == AccountMatcher.Normalize(focus.AccountName)) ??
                  _all.FirstOrDefault(r => r.Row.Duplicates?.Candidates.Any(c => ReferenceEquals(c.Mafile, focus)) == true);
        if (row == null) return;
        SelectedFilter = Filters[0];
        SearchText = focus.AccountName;
        SelectedRow = row;
        if (_owner.LinkIntent == true && row.IsLinkable) row.IsChecked = true;
        if (_owner.LinkIntent == false && row.IsLinked) row.IsChecked = true;
        NarrowDetailsOpen = true;
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void ClearSelection()
    {
        _bulkChecking = true;
        try
        {
            foreach (var row in _all.Where(r => r.IsChecked)) row.IsChecked = false;
        }
        finally
        {
            _bulkChecking = false;
        }

        RaiseSelection();
    }

    /// <summary>Details panel: connect only this account (the overlay shows the grants first).</summary>
    [RelayCommand]
    private void ConnectRow(AccountRowVM? row)
    {
        if (row is not {IsLinkable: true}) return;
        ClearSelection();
        row.IsChecked = true;
        if (ConnectSelectedCommand.CanExecute(null)) ConnectSelected();
    }

    [RelayCommand]
    private void DisconnectRow(AccountRowVM? row)
    {
        if (row is not {IsLinked: true}) return;
        ClearSelection();
        row.IsChecked = true;
        if (DisconnectSelectedCommand.CanExecute(null)) DisconnectSelected();
    }

    public bool HasVisibleRows => VisibleRows.Count > 0;
    public bool HasChecked => CheckedCount > 0;

    partial void OnVisibleRowsChanged(IReadOnlyList<AccountRowVM> value)
    {
        OnPropertyChanged(nameof(HasVisibleRows));
        OnPropertyChanged(nameof(AllVisibleChecked));
    }

    private bool CanConnect() => IsOnline && CheckedLinkable > 0 && !IsBusy;
    private bool CanDisconnect() => IsOnline && CheckedLinked > 0 && !IsBusy;
    private bool CanChangePermissions() => CheckedLinked > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void ConnectSelected()
    {
        ConnectGrants = BuildOverlayGrants(_ => false);
        OverlayError = null;
        Overlay = ManageOverlay.Connect;
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void DisconnectSelected()
    {
        DisconnectHasNebula = _all.Any(r => r.IsChecked && r.IsLinked &&
                                            r.Server?.Source is null or NebulaAuthenticatorSources.Nebula);
        OverlayError = null;
        Overlay = ManageOverlay.Disconnect;
    }

    [RelayCommand(CanExecute = nameof(CanChangePermissions))]
    private void ChangePermissions()
    {
        var linked = _all.Where(r => r.IsChecked && r.IsLinked).ToList();
        PermissionGrants = BuildOverlayGrants(k => linked.Count > 0 && linked.All(r => r.Link!.Grants.Contains(k)));
        OverlayError = null;
        Overlay = ManageOverlay.Permissions;
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        Overlay = ManageOverlay.None;
        Password = "";
    }

    [RelayCommand]
    private async Task ConfirmConnect()
    {
        var rows = _all.Where(r => r.IsChecked && r.IsLinkable).Select(r => r.Row).ToList();
        var grants = Optional(ConnectGrants).Where(g => g.IsOn).Select(g => g.Key!).ToList();
        await RunBulk(() => Integration.SetAccountLinksAsync(rows, true, grants, CancellationToken.None), true);
    }

    [RelayCommand]
    private async Task ConfirmDisconnect()
    {
        var rows = _all.Where(r => r.IsChecked && r.IsLinked).Select(r => r.Row).ToList();
        await RunBulk(() => Integration.SetAccountLinksAsync(rows, false, [], CancellationToken.None), false);
    }

    [RelayCommand]
    private async Task ApplyPermissions()
    {
        var linked = _all.Where(r => r.IsChecked && r.IsLinked).ToList();
        var logins = linked.Select(r => r.Link!.Login).ToList();
        IsBusy = true;
        try
        {
            foreach (var g in Optional(PermissionGrants))
            {
                var allOn = linked.All(r => r.Link!.Grants.Contains(g.Key!));
                var noneOn = linked.All(r => !r.Link!.Grants.Contains(g.Key!));
                if ((g.IsOn && allOn) || (!g.IsOn && noneOn)) continue;
                await Integration.SetGrantAsync(logins, g.Key!, g.IsOn, CancellationToken.None);
            }

            BulkResult = NebulaLinkText.Format("Permissions saved: {0}", ["Manage", "PermissionsSaved"], linked.Count);
            BulkResultHint = null;
            Overlay = ManageOverlay.None;
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    /// <summary>"login: reason" of the skipped accounts (at most 10 lines).</summary>
    private string? SkipReasons(BulkOutcome outcome)
    {
        if (outcome.Reasons.Count == 0) return null;
        var lines = outcome.Reasons.Take(10).Select(r =>
        {
            var login = Integration.ServerAccount(r.Key)?.Login ?? "#" + r.Key;
            var reason = r.Value switch
            {
                nameof(MatchKind.OnlyLocal) => NebulaLinkText.Get("Not in Achiefy", "Status", "OnlyLocal"),
                nameof(MatchKind.OnlyAchiefy) => NebulaLinkText.Get("No mafile here", "Status", "OnlyAchiefy"),
                nameof(MatchKind.DuplicateMafile) => NebulaLinkText.Get("Duplicate mafile", "Status", "Duplicate"),
                _ => NebulaLinkText.LinkResult(r.Value)
            };
            return login + ": " + reason;
        }).ToList();
        if (outcome.Reasons.Count > 10) lines.Add("…");
        return string.Join("\n", lines);
    }

    private async Task RunBulk(Func<Task<BulkOutcome>> action, bool link)
    {
        IsBusy = true;
        OverlayError = null;
        try
        {
            var outcome = await action();
            if (outcome.Error != null && outcome.Changed == 0)
            {
                OverlayError = NebulaLinkText.Result(outcome.Error);
                return;
            }

            BulkResult = link
                ? NebulaLinkText.Format("Connected: {0}", ["Manage", "ConnectedCount"], outcome.Changed)
                : NebulaLinkText.Format("Disconnected: {0}", ["Manage", "DisconnectedCount"], outcome.Changed);
            if (outcome.Skipped > 0)
                BulkResult += " · " + NebulaLinkText.Format("Skipped: {0}", ["Manage", "SkippedCount"], outcome.Skipped);
            BulkResultHint = SkipReasons(outcome);
            Overlay = ManageOverlay.None;
            ClearSelection();
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Nebula Link bulk change failed");
            OverlayError = NebulaLinkText.Result(NebulaResultCodes.InternalError);
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    private async void OnGrantToggled(GrantVM grant, bool value)
    {
        var row = SelectedRow;
        if (row?.Link == null) return;
        try
        {
            await Integration.SetGrantAsync([row.Link.Login], grant.Key!, value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Grant change failed");
            grant.Revert(!value);
        }
    }

    /// <summary>"Use this one" on a candidate of a "Duplicate mafile" row: stored in nebula.json, the row is matched again.</summary>
    [RelayCommand]
    private async Task UseDuplicate(DuplicateCandidateVM? candidate)
    {
        if (candidate is not {CanUse: true}) return;
        try
        {
            await Integration.ChooseDuplicateAsync(candidate.Login, candidate.FileName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Duplicate mafile choice failed");
        }
        finally
        {
            Rebuild();
        }
    }

    [RelayCommand]
    private void UnlinkDevice()
    {
        OverlayError = null;
        Overlay = ManageOverlay.UnlinkDevice;
    }

    [RelayCommand]
    private async Task ConfirmUnlinkDevice()
    {
        IsBusy = true;
        try
        {
            var acknowledged = await Integration.UnlinkAsync();
            Overlay = ManageOverlay.None;
            SnackbarController.SendSnackbar(acknowledged
                ? NebulaLinkText.Get("This computer was unlinked.", "Manage", "Unlinked")
                : NebulaLinkText.Get(
                    "Unlinked here only: Achiefy was offline. Unlink it there too.",
                    "Manage", "UnlinkedOffline"), TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowDetails(AccountRowVM? row)
    {
        if (row != null) SelectedRow = row;
        NarrowDetailsOpen = true;
    }

    [RelayCommand]
    private void BackToList()
    {
        NarrowDetailsOpen = false;
    }

    [RelayCommand]
    private void UpdatePassword()
    {
        Password = "";
        SavePassword = SteamPasswords.CanSave;
        OverlayError = null;
        Overlay = ManageOverlay.Password;
    }

    public bool CanSavePasswords => SteamPasswords.CanSave;

    [RelayCommand]
    private async Task ConfirmPassword()
    {
        var mafile = SelectedRow?.Local;
        if (mafile == null || string.IsNullOrEmpty(Password)) return;
        IsBusy = true;
        OverlayError = null;
        try
        {
            await MaClient.LoginAgain(mafile, Password, SavePassword);
            Overlay = ManageOverlay.None;
            Password = "";
            SnackbarController.SendSnackbar(LocManager.GetCodeBehindOrDefault("SuccessfulLogin", "MainVM", "SuccessfulLogin"));
        }
        catch (LoginException ex)
        {
            OverlayError = ErrorTranslatorHelper.TranslateLoginError(ex.Error);
        }
        catch (Exception ex)
        {
            OverlayError = ExceptionHandler.GetExceptionString(ex) ?? ex.Message;
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    #endregion

    #region History

    private void RebuildHistory()
    {
        var logins = new Dictionary<int, string>();
        foreach (var a in Integration.ServerAccounts()) logins[a.AccountId] = a.Login;
        var byLogin = Integration.ResolveLocal().ByLogin;
        var catalog = Integration.ModuleCatalog;
        var requests = Integration.History.Requests().Take(500).ToList();
        var manual = ManualDecisions(requests);
        HistoryRows = requests
            .Select(r => new HistoryRowVM(r, logins.GetValueOrDefault(r.AccountId) ??
                                             (r.Login != null && byLogin.TryGetValue(r.Login, out var m) ? m.AccountName : "#" + r.AccountId),
                catalog, manual.GetValueOrDefault(r)))
            .ToList();
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>
    ///     The user's manual decisions on operations Achiefy asked about, each on the newest request row of its operation
    ///     (a decision without such a row in the local history is not shown).
    /// </summary>
    private Dictionary<RequestRecord, DecisionRecord> ManualDecisions(List<RequestRecord> requests)
    {
        var result = new Dictionary<RequestRecord, DecisionRecord>(ReferenceEqualityComparer.Instance);
        foreach (var decision in Integration.History.Decisions().Where(d => d.Origin == DecisionRecord.ORIGIN_MANUAL))
        {
            var operationType = ConfirmationTypes.ToOperationType(decision.Type);
            var row = requests.Where(r => r.Login == decision.Login && r.OperationType == operationType &&
                                          r.Ids?.Contains(decision.CreatorId) == true)
                .MaxBy(r => r.Time);
            if (row != null && (!result.TryGetValue(row, out var other) || other.Time < decision.Time)) result[row] = decision;
        }

        return result;
    }

    public bool HasHistory => HistoryRows.Count > 0;

    #endregion

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommands();
    }

    partial void OnIsNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSidePanel));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowNarrowDetails));
    }

    partial void OnNarrowDetailsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowNarrowDetails));
    }

    partial void OnOverlayChanged(ManageOverlay value)
    {
        OnPropertyChanged(nameof(HasOverlay));
    }

    public bool HasOverlay => Overlay != ManageOverlay.None;

    /// <summary>Wide: details at the side. Narrow: the details replace the list (back button).</summary>
    public bool ShowSidePanel => !IsNarrow;

    public bool ShowListPane => !IsNarrow || !NarrowDetailsOpen;
    public bool ShowNarrowDetails => IsNarrow && NarrowDetailsOpen;
}
