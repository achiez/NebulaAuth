using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using NebulaAuth.Model.Entities;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Protocol;
using SteamLib;

namespace NebulaAuth.ViewModel.NebulaLink.Pages;

/// <summary>Color of a row status: Ok (ready), Warn (needs the user), Bad (error, blocked), Muted (not connected).</summary>
public enum RowStatusKind
{
    Muted,
    Ok,
    Warn,
    Bad
}

/// <summary>One row of the accounts table (account / connection / status / permissions / last request).</summary>
public partial class AccountRowVM : ObservableObject
{
    /// <summary>Capabilities every connected account has (shown as checked, disabled permissions; Scope.* texts).</summary>
    public static readonly string[] MandatoryScopes = ["login", "confirmation_check"];

    private static readonly string[] DisplayProperties =
    [
        nameof(Row), nameof(Link), nameof(AccountState), nameof(LinkFailure), nameof(LastRequest), nameof(Local),
        nameof(Server), nameof(Login), nameof(IsLinked), nameof(IsLinkable), nameof(InAchiefy), nameof(NeedsLogin), nameof(IsError), nameof(InvalidPassword), nameof(StatusKind), nameof(StatusText),
        nameof(StatusHint), nameof(StatusOk), nameof(StatusWarn), nameof(StatusBad), nameof(StatusMuted),
        nameof(PermissionsText), nameof(PermissionsHint), nameof(LastRequestText), nameof(LastRequestHint),
        nameof(SteamIdText), nameof(HasStatusHint), nameof(IsDuplicate), nameof(DuplicateCandidates)
    ];

    [ObservableProperty] private bool _isChecked;

    public AccountRowVM(MatchRow row, NebulaAccountLink? link, string? accountState, string? linkFailure,
        RequestRecord? lastRequest)
    {
        Row = row;
        Link = link;
        AccountState = accountState;
        LinkFailure = linkFailure;
        LastRequest = lastRequest;
        Key = row.Login;
        SearchText = BuildSearchText(row);
    }

    public MatchRow Row { get; private set; }
    public NebulaAccountLink? Link { get; private set; }
    public string? AccountState { get; private set; }
    public string? LinkFailure { get; private set; }
    public RequestRecord? LastRequest { get; private set; }
    public string Key { get; }
    public string SearchText { get; private set; }

    public Mafile? Local => Row.Local as Mafile;
    public AchiefyAccount? Server => Row.Server;
    public string Login => Row.Local?.AccountName ?? Row.Server?.Login ?? "";
    public bool IsLinked => Link != null;

    /// <summary>Can be connected now: unique match and not linked yet.</summary>
    public bool IsLinkable => Row.IsLinkable && !IsLinked;

    public bool InAchiefy => Row.Server != null;

    /// <summary>Several local mafiles have this login: the details list them with "Use this one".</summary>
    public bool IsDuplicate => Row.Kind == MatchKind.DuplicateMafile;

    public IReadOnlyList<DuplicateCandidateVM> DuplicateCandidates =>
        Row.Duplicates?.Candidates.Select(c => new DuplicateCandidateVM(Login, c)).ToList() ?? [];

    public bool NeedsLogin => AccountState is NebulaAccountStates.SteamSessionRequired or NebulaAccountStates.InvalidLocalPassword;

    public bool IsError => StatusKind == RowStatusKind.Bad;

    public bool InvalidPassword => AccountState == NebulaAccountStates.InvalidLocalPassword;

    private static string BuildSearchText(MatchRow row)
    {
        return (row.Local?.AccountName ?? row.Server?.Login ?? "").ToLowerInvariant() + " " + LocalSteamId(row.Local);
    }

    /// <summary>SteamID64 of the local mafile (display and search only).</summary>
    private static string? LocalSteamId(MobileDataExtended? mafile)
    {
        if (mafile == null) return null;
        var id = (mafile.SessionData?.SteamId ?? mafile.SteamId).Steam64.ToUlong();
        return id == 0 ? null : id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Updates the row in place (the list keeps its scroll position and selection).</summary>
    public bool Update(MatchRow row, NebulaAccountLink? link, string? accountState, string? linkFailure,
        RequestRecord? lastRequest)
    {
        var same = Equals(row, Row) && LinkEquals(link, Link) && accountState == AccountState &&
                   linkFailure == LinkFailure && ReferenceEquals(lastRequest, LastRequest);
        if (same) return false;
        Row = row;
        Link = link;
        AccountState = accountState;
        LinkFailure = linkFailure;
        LastRequest = lastRequest;
        SearchText = BuildSearchText(row);
        foreach (var name in DisplayProperties) OnPropertyChanged(name);
        return true;
    }

    private static bool LinkEquals(NebulaAccountLink? a, NebulaAccountLink? b)
    {
        if (a == null || b == null) return a == b;
        return a.AccountId == b.AccountId && a.Login == b.Login && a.Grants.SequenceEqual(b.Grants);
    }

    #region Status

    public RowStatusKind StatusKind
    {
        get
        {
            if (IsDuplicate) return RowStatusKind.Bad;
            if (IsLinked) return KindOf(AccountState);
            if (LinkFailure != null) return RowStatusKind.Bad;
            return RowStatusKind.Muted;
        }
    }

    /// <summary>Ok / Warn / Bad of a linked account state (the table rows and the account list icons).</summary>
    public static RowStatusKind KindOf(string? accountState)
    {
        return accountState switch
        {
            null or NebulaAccountStates.Ready => RowStatusKind.Ok,
            NebulaAccountStates.SteamSessionRequired => RowStatusKind.Warn,
            _ => RowStatusKind.Bad
        };
    }

    public bool StatusOk => StatusKind == RowStatusKind.Ok;
    public bool StatusWarn => StatusKind == RowStatusKind.Warn;
    public bool StatusBad => StatusKind == RowStatusKind.Bad;
    public bool StatusMuted => StatusKind == RowStatusKind.Muted;

    public string StatusText
    {
        get
        {
            if (IsDuplicate) return NebulaLinkText.Get("Duplicate mafile", "Status", "Duplicate");
            if (IsLinked)
            {
                return AccountState is null or NebulaAccountStates.Ready
                    ? NebulaLinkText.Get("Ready", "Status", "Ready")
                    : NebulaLinkText.AccountState(AccountState);
            }

            if (LinkFailure != null) return NebulaLinkText.LinkResult(LinkFailure);
            return Row.Kind switch
            {
                MatchKind.OnlyLocal => NebulaLinkText.Get("Not in Achiefy", "Status", "OnlyLocal"),
                MatchKind.OnlyAchiefy => NebulaLinkText.Get("No mafile here", "Status", "OnlyAchiefy"),
                _ => NebulaLinkText.Get("Not connected", "Status", "NotLinked")
            };
        }
    }

    /// <summary>What to do, only where the status alone does not say it (row tooltip, details panel).</summary>
    public string? StatusHint => IsLinked && AccountState == NebulaAccountStates.SteamSessionRequired
        ? NebulaLinkText.AccountStateHint(AccountState)
        : null;

    public bool HasStatusHint => StatusHint != null;

    #endregion

    #region Columns

    /// <summary>The module catalog the permissions are shown with (set by the page).</summary>
    public NebulaModuleCatalog Catalog
    {
        get => _catalog;
        set
        {
            if (ReferenceEquals(_catalog, value)) return;
            _catalog = value;
            OnPropertyChanged(nameof(PermissionsText));
            OnPropertyChanged(nameof(PermissionsHint));
        }
    }

    private NebulaModuleCatalog _catalog = ModuleCatalogs.Empty;

    /// <summary>Permissions column: the number of permissions of a connected account ("—" otherwise).</summary>
    public string PermissionsText => IsLinked
        ? (MandatoryScopes.Length + ActiveGrants().Count()).ToString(CultureInfo.CurrentCulture)
        : "—";

    /// <summary>The permissions as a list (tooltip of the permissions cell).</summary>
    public string? PermissionsHint => IsLinked
        ? string.Join("\n", MandatoryScopes.Select(NebulaLinkText.Scope).Concat(ActiveGrants().Select(m =>
            NebulaLinkText.Category(m.Category) + ": " + ModuleCatalogs.DisplayName(m, NebulaLinkText.LanguageCode))))
        : null;

    /// <summary>Granted modules that are in the current catalog.</summary>
    private IEnumerable<NebulaModuleInfo> ActiveGrants()
    {
        return Catalog.Modules.Where(m => Link!.Grants.Contains(new GrantKey(m.Category, m.Id)));
    }

    /// <summary>Last request column: how long ago only ("5 min", "2 h", "yesterday"); the details are in the tooltip.</summary>
    public string LastRequestText => LastRequest == null ? "—" : NebulaLinkText.Ago(LastRequest.Time);

    /// <summary>The request, its exact time and result (one fact per line).</summary>
    public string? LastRequestHint => LastRequest == null
        ? null
        : string.Join("\n", NebulaLinkText.Command(LastRequest.Type),
            LastRequest.Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture),
            NebulaLinkText.Result(LastRequest.Result));

    #endregion

    #region Details

    /// <summary>SteamID64 of the local mafile, null when there is none.</summary>
    public string? SteamIdText => LocalSteamId(Row.Local);

    /// <summary>Permissions of the details panel: the mandatory ones (checked, disabled), then the catalog by category.</summary>
    public IReadOnlyList<PermissionGroupVM> Grants { get; private set; } = [];

    public void BuildGrants(Action<GrantVM, bool> onChanged)
    {
        Grants = PermissionGroupVM.Build(Catalog, IsLinked, IsLinked, k => Link?.Grants.Contains(k) == true, onChanged);
        OnPropertyChanged(nameof(Grants));
    }

    #endregion
}

/// <summary>
///     A permission of one account: a module of the server catalog (<see cref="Key" />), or a mandatory capability that is
///     always on and can't be changed.
/// </summary>
public partial class GrantVM : ObservableObject
{
    private readonly Action<GrantVM, bool>? _changed;
    private bool _suppress;
    [ObservableProperty] private bool _isOn;

    public GrantVM(GrantKey? key, string label, bool isOn, bool isEnabled, Action<GrantVM, bool>? changed)
    {
        Key = key;
        Label = label;
        _isOn = isOn;
        IsEnabled = isEnabled;
        _changed = changed;
    }

    public static GrantVM Mandatory(string scope, bool on)
    {
        return new GrantVM(null, NebulaLinkText.Scope(scope), on, false, null) {IsMandatory = true};
    }

    /// <summary>(category, module id); null for a mandatory capability.</summary>
    public GrantKey? Key { get; }

    /// <summary>Module name (plain text from the catalog, length-limited) or the capability text.</summary>
    public string Label { get; }

    public bool IsEnabled { get; }
    public bool IsMandatory { get; private init; }


    partial void OnIsOnChanged(bool value)
    {
        if (!_suppress) _changed?.Invoke(this, value);
    }

    /// <summary>Restores the toggle without reporting (a failed save).</summary>
    public void Revert(bool value)
    {
        _suppress = true;
        IsOn = value;
        _suppress = false;
    }
}

/// <summary>Permissions of one category (or the mandatory ones) with a title.</summary>
public sealed class PermissionGroupVM(string title, IReadOnlyList<GrantVM> items)
{
    public string Title { get; } = title;
    public IReadOnlyList<GrantVM> Items { get; } = items;

    /// <summary>
    ///     The mandatory capabilities (checked when <paramref name="mandatoryOn" />, always disabled), then one group per
    ///     category of the catalog with its modules in catalog order. An empty catalog leaves only the first group.
    /// </summary>
    public static IReadOnlyList<PermissionGroupVM> Build(NebulaModuleCatalog catalog, bool mandatoryOn, bool enabled,
        Func<GrantKey, bool> isOn, Action<GrantVM, bool>? changed)
    {
        var language = NebulaLinkText.LanguageCode;
        var groups = new List<PermissionGroupVM>
        {
            new(NebulaLinkText.Get("Always on", "Category", "mandatory"),
                AccountRowVM.MandatoryScopes.Select(s => GrantVM.Mandatory(s, mandatoryOn)).ToList())
        };
        foreach (var category in NebulaCategories.All)
        {
            var items = catalog.Modules.Where(m => m.Category == category)
                .Select(m =>
                {
                    var key = new GrantKey(m.Category, m.Id);
                    return new GrantVM(key, ModuleCatalogs.DisplayName(m, language), isOn(key), enabled, changed);
                })
                .ToList();
            if (items.Count > 0) groups.Add(new PermissionGroupVM(NebulaLinkText.Category(category), items));
        }

        return groups;
    }

    public static IEnumerable<GrantVM> Optional(IEnumerable<PermissionGroupVM> groups)
    {
        return groups.SelectMany(g => g.Items).Where(i => i.Key != null);
    }
}

/// <summary>One of several local mafiles of a login (details panel of a "Duplicate mafile" row).</summary>
public sealed class DuplicateCandidateVM(string login, MafileCandidate candidate)
{
    public string Login { get; } = login;
    public string FileName { get; } = candidate.FileName ?? "—";
    public bool CanUse => candidate.FileName != null;
    public string SteamIdText => candidate.SteamId != null ? "SteamID " + candidate.SteamId : "SteamID —";

    public string ModifiedText => candidate.Modified is { } m
        ? NebulaLinkText.Format("Changed {0}", ["Duplicate", "Modified"], m.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))
        : "";

    public bool HasSession => candidate.HasSession;

    public string SessionText => candidate.HasSession
        ? NebulaLinkText.Get("Session active", "Duplicate", "SessionActive")
        : NebulaLinkText.Get("No session", "Duplicate", "NoSession");
}

/// <summary>A row of the request history.</summary>
public sealed class HistoryRowVM(RequestRecord record, string account, NebulaModuleCatalog catalog, DecisionRecord? manual = null)
{
    public string Time => record.Time.ToLocalTime().ToString("dd.MM HH:mm:ss");
    public string Account { get; } = account;
    public string Module { get; } = NebulaLinkText.Module(catalog, record.Module);

    /// <summary>The operation ("Trade", "Listing ×3") or, without one, the command.</summary>
    public string Type => record.OperationType != null
        ? NebulaLinkText.Operation(record.OperationType) + (record.IdCount > 1 ? $" ×{record.IdCount}" : "")
        : NebulaLinkText.Command(record.Type);

    /// <summary>Command, operation and decision, one per line.</summary>
    public string TypeHint => string.Join("\n", new[]
    {
        NebulaLinkText.Command(record.Type),
        record.OperationType != null ? Type : null,
        record.Decision != null ? NebulaLinkText.Get(record.Decision, "Decision", record.Decision) : null
    }.OfType<string>());

    /// <summary>The result, or the user's own decision in NebulaAuth on this operation ("Confirmed by you").</summary>
    public string Result => manual == null
        ? NebulaLinkText.Result(record.Result)
        : manual.Accepted
            ? NebulaLinkText.Get("Confirmed by you", "History", "ConfirmedByYou")
            : NebulaLinkText.Get("Declined by you", "History", "DeclinedByYou");

    /// <summary>With a manual decision: the result Achiefy got and when the user decided.</summary>
    public string ResultHint => manual == null
        ? Result
        : Result + " · " + manual.Time.ToLocalTime().ToString("dd.MM HH:mm:ss") + "\n" + NebulaLinkText.Result(record.Result);

    public bool ResultOk => manual != null || record.Result is NebulaResultCodes.Ok or NebulaResultCodes.Pending
        or NebulaResultCodes.Confirmed or NebulaResultCodes.Declined;

    public bool ResultBad => !ResultOk && record.Result != NebulaResultCodes.NotFound;
}

public sealed record FilterOption(string Key, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}
