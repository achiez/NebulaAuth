using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Credentials;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.NebulaLink.Sessions;
using SteamLib;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Nebula Link (Nebula Protocol): the composition of pairing (<see cref="PairingFlow" />), the device credential
///     (<see cref="DeviceCredentialManager" />), the link and its connection (<see cref="LinkSession" />), the Achiefy
///     accounts (<see cref="ServerAccountMirror" />), the account links (<see cref="AccountLinkService" />), the state
///     reports (<see cref="AccountStateReporter" />) and the execution of Achiefy requests. This class only wires them and
///     exposes what the app needs. Nothing network-related happens before the user paired: no HttpClient, no credential,
///     no connection, no account data.
/// </summary>
public sealed partial class NebulaLinkIntegration : INebulaExecutorContext, IConfirmationLinkInfo
{
    private readonly IHeadlessSessionBackend _sessionBackend;
    private readonly DeviceCredentialManager _credentials;
    private readonly ServerAccountMirror _mirror;
    private readonly AccountLinkService _linkService;
    private readonly AccountStateReporter _reporter;
    private readonly PairingFlow _pairing;
    private readonly LinkSession _session;

    public NebulaLinkIntegration(NebulaLinkOptions options)
    {
        _sessionBackend = options.SessionBackend;
        var clock = options.Clock ?? (() => DateTimeOffset.UtcNow);
        Links = new LinkStore(options.DataDirectory == null ? null : Path.Combine(options.DataDirectory, LinkStore.FILE_NAME));
        History = new NebulaLinkHistoryStore(
            options.DataDirectory == null ? null : Path.Combine(options.DataDirectory, NebulaLinkHistoryStore.FILE_NAME), clock);
        ClientPool = options.ClientPool ?? new AccountSteamClientPool();
        GuardCodes = options.GuardCodes ?? new GuardCodeService();
        Headless = new HeadlessSessionPolicy(_sessionBackend);
        Confirmations = new ConfirmationService(
            options.ConfirmationTransport ?? new SteamConfirmationTransport(ClientPool, Headless, options.InteractiveSessions),
            History, this, clock);

        _credentials = new DeviceCredentialManager(options.Selector, Links, options.PurgeEncryptedCredential);
        _mirror = new ServerAccountMirror(Links);
        LinkSession? session = null;
        _linkService = new AccountLinkService(Links, _mirror, Headless, Confirmations, ClientPool, options.Mafiles,
            options.FileInfo, () => session!.Catalog, () => session!.Connection, options.AccountLinked, clock, RaiseChanged);
        _reporter = new AccountStateReporter(() => session!.Connection, _linkService.StateItems);
        _linkService.Reporter = _reporter;
        session = new LinkSession(Links, History, _credentials, _mirror, _linkService, _reporter, options.Http, clock,
            RaiseChanged);
        _session = session;
        _pairing = new PairingFlow(options.Http, Links, _credentials, clock, session.OnPaired);
        Executor = new NebulaRequestExecutor(this, GuardCodes, Confirmations, History, async result =>
        {
            await session.SendResultAsync(result);
            // A request can change the account state (e.g. a rejected saved password): the state report follows
            if (result.AccountState != null) _reporter.NotifyLocalChange();
        }, clock);
        session.Executor = Executor;

        Confirmations.EventsQueued += () => _ = session.FlushEventsAsync();
        // A new proxy means a new client: the list read through the old one is not reused
        ClientPool.ProxyChanged += key => Confirmations.Invalidate(key);
        History.Changed += () => RaiseChanged(NebulaLinkChange.History);
    }

    partial void DebugOnInitialized();

    public LinkStore Links { get; }
    public NebulaLinkHistoryStore History { get; }
    public ConfirmationService Confirmations { get; }
    public NebulaRequestExecutor Executor { get; }
    public HeadlessSessionPolicy Headless { get; }

    /// <summary>The Steam clients of the accounts (shared by the manual screen and Achiefy requests).</summary>
    public AccountSteamClientPool ClientPool { get; }

    /// <summary>The login code registry (one code per login and 30 s window, shared with the app's own logins).</summary>
    public GuardCodeService GuardCodes { get; }

    /// <summary>The user accepted the intro in this session (nothing is stored before a successful pairing).</summary>
    public bool ConsentGiven { get; private set; }

    public NebulaLinkState State => _session.State;

    /// <summary>Reason of the current state (revoke / link state reason).</summary>
    public string? StateReason => _session.StateReason;

    /// <summary>Incompatible: the server's major is newer (update NebulaAuth) or older (Achiefy does not support it yet).</summary>
    public bool ServerNewer => _session.ServerNewer;

    /// <summary>The Achiefy accounts have been loaded from the server since the link was established.</summary>
    public bool ServerAccountsLoaded => _mirror.Loaded;

    public PendingPairing? Pairing => _pairing.Pairing;

    /// <summary>Origin of the current link (stored at pairing), e.g. "https://example.com"; null before pairing.</summary>
    public string? LinkOrigin
    {
        get
        {
            var url = Links.Snapshot()?.TenantBaseUrl;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? NebulaLinkApis.GetOrigin(uri) : null;
        }
    }

    /// <summary>
    ///     The server module catalog (sanitized): every entry is one optional permission per account. Received with
    ///     Hello and ModuleCatalogChanged, kept in nebula.json for the time NebulaAuth is offline.
    /// </summary>
    public NebulaModuleCatalog ModuleCatalog => _session.Catalog;

    /// <summary>Raised on any change (any thread; the app marshals and coalesces it for the UI).</summary>
    public event Action<NebulaLinkChange>? Changed;

    /// <summary>
    ///     Reads nebula.json and nebula-history.json. Starts the connection only when paired, not paused and the
    ///     credential is present; otherwise nothing network-related happens.
    /// </summary>
    public void Initialize()
    {
        Links.Load();
        History.Load();
        History.Persist = Links.HasLink;
        Links.Changed += () => RaiseChanged(NebulaLinkChange.Accounts);
        DebugOnInitialized();
        _session.Start();
    }

    /// <summary>App exit: flushes the history and closes the connection.</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            History.Flush();
            await _session.ShutdownAsync();
        }
        catch
        {
            // exiting
        }
    }

    #region Pairing and link

    /// <summary>Which secure store would hold the credential (null: none, pairing is blocked).</summary>
    public Task<IDeviceCredentialStore?> ProbeCredentialStoreAsync() => _credentials.ProbeAsync();

    /// <summary>
    ///     The user accepted the intro. Kept in memory only: nebula.json, the history and the credential are written
    ///     after a successful pairing, so a failed or cancelled pairing leaves nothing behind.
    /// </summary>
    public void Enable()
    {
        ConsentGiven = true;
    }

    /// <inheritdoc cref="PairingFlow.RedeemAsync(string, CancellationToken)" />
    public Task<PendingPairing> RedeemAsync(string input, CancellationToken ct) => _pairing.RedeemAsync(input, ct);

    /// <inheritdoc cref="PairingFlow.RedeemAsync(PairingTarget, CancellationToken)" />
    public Task<PendingPairing> RedeemAsync(PairingTarget target, CancellationToken ct) => _pairing.RedeemAsync(target, ct);

    public Task CancelPairingAsync() => _pairing.CancelAsync();

    /// <inheritdoc cref="PairingFlow.WaitForApprovalAsync" />
    public Task<PairingWaitResult> WaitForApprovalAsync(CancellationToken ct) => _pairing.WaitForApprovalAsync(ct);

    public Task PauseAsync() => _session.PauseAsync();
    public Task ResumeAsync() => _session.ResumeAsync();

    /// <inheritdoc cref="LinkSession.ReconnectAsync" />
    public Task ReconnectAsync() => _session.ReconnectAsync();

    /// <inheritdoc cref="LinkSession.UnlinkAsync" />
    public Task<bool> UnlinkAsync() => _session.UnlinkAsync();

    public void AcknowledgeRevoked() => _session.AcknowledgeRevoked();

    /// <inheritdoc cref="LinkSession.OnStorageModeChangedAsync" />
    public Task OnStorageModeChangedAsync(bool encryptionOff) => _session.OnStorageModeChangedAsync(encryptionOff);

    #endregion

    #region Accounts

    public IReadOnlyList<AchiefyAccount> ServerAccounts() => _mirror.Accounts();
    public AchiefyAccount? ServerAccount(int accountId) => _mirror.Account(accountId);

    /// <summary>Last link / unlink failure of an account in this session (protocol result string).</summary>
    public string? LinkFailure(int accountId) => _linkService.LinkFailure(accountId);

    /// <summary>Local matching of the mafiles with the loaded Achiefy accounts.</summary>
    public IReadOnlyList<MatchRow> MatchRows() => _linkService.MatchRows();

    /// <summary>The local accounts with duplicate mafiles resolved (one mafile per login, see <see cref="LocalAccounts" />).</summary>
    public LocalAccounts ResolveLocal() => _linkService.ResolveLocal();

    /// <summary>State of a linked account as reported to the server (<see cref="NebulaAccountStates" />).</summary>
    public string ComputeAccountState(NebulaAccountLink link, LocalAccounts index) => _linkService.ComputeAccountState(link, index);

    /// <summary>State of every linked account by normalized login.</summary>
    public IReadOnlyDictionary<string, string> AccountStates() => _linkService.AccountStates();

    /// <summary>Linked accounts that need attention + failed account links of this session.</summary>
    public int ProblemsCount() => _linkService.ProblemsCount();

    /// <inheritdoc cref="AccountLinkService.SetAccountLinksAsync" />
    public Task<BulkOutcome> SetAccountLinksAsync(IReadOnlyList<MatchRow> rows, bool link, IReadOnlyCollection<GrantKey> grants,
        CancellationToken ct) => _linkService.SetAccountLinksAsync(rows, link, grants, ct);

    /// <inheritdoc cref="AccountLinkService.SetGrantAsync" />
    public Task<BulkOutcome> SetGrantAsync(IReadOnlyCollection<string> logins, GrantKey scope, bool on, CancellationToken ct) =>
        _linkService.SetGrantAsync(logins, scope, on, ct);

    /// <inheritdoc cref="AccountLinkService.ChooseDuplicateAsync" />
    public Task ChooseDuplicateAsync(string login, string fileName, CancellationToken ct) =>
        _linkService.ChooseDuplicateAsync(login, fileName, ct);

    /// <summary>
    ///     The local state of accounts may have changed (session, mafile, saved password...): the UI refreshes and the
    ///     accounts whose state changed are reported (coalesced). Any thread; never blocks.
    /// </summary>
    public void NotifyLocalChange()
    {
        RaiseChanged(NebulaLinkChange.Accounts);
        _reporter.NotifyLocalChange();
    }

    /// <summary>Reports the linked accounts whose state changed since the last report; the number sent.</summary>
    public Task<int> ReportChangedStatesAsync() => _reporter.ReportChangedAsync();

    /// <summary>The account of this mafile is linked under the current link.</summary>
    public bool IsLinked(MobileDataExtended? mafile)
    {
        return mafile != null && Links.IsLinked(AccountMatcher.Normalize(mafile.AccountName));
    }

    #endregion

    #region INebulaExecutorContext / IConfirmationLinkInfo

    bool INebulaExecutorContext.IsPaused => Links.Snapshot()?.Paused == true;

    NebulaAccountLink? INebulaExecutorContext.GetAccountLink(int accountId)
    {
        return Links.HasLink ? Links.FindByAccountId(accountId) : null;
    }

    LocalAccounts INebulaExecutorContext.LocalAccounts() => ResolveLocal();

    NebulaModuleCatalog INebulaExecutorContext.Catalog => _session.Catalog;

    bool INebulaExecutorContext.IsInvalidPassword(MobileDataExtended mafile) => _sessionBackend.IsInvalidPasswordFlagged(mafile);

    string INebulaExecutorContext.CheckReady(MobileDataExtended mafile) => Headless.CheckReady(mafile);

    int? IConfirmationLinkInfo.GetLinkedAccountId(string accountKey)
    {
        return Links.IsLinked(accountKey) ? Links.FindByLogin(accountKey)?.AccountId : null;
    }

    #endregion

    private void RaiseChanged(NebulaLinkChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Error("Nebula Link change handler failed", ex);
        }
    }
}
