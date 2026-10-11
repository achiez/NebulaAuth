using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Protocol;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     The device link: its state machine (<see cref="NebulaLinkState" />), the connection to the device hub and the
///     handling of its events (Hello, commands, account changes, link state, catalog), pause / resume / reconnect, unlink
///     and revoke. Events of a replaced connection are ignored.
/// </summary>
internal sealed partial class LinkSession
{
    private const int MAX_UNAUTHORIZED_ATTEMPTS = 3;
    private static readonly TimeSpan IncompatibleRetry = TimeSpan.FromHours(6);

    private readonly Func<DateTimeOffset> _clock;
    private readonly DeviceCredentialManager _credentials;
    private readonly NebulaLinkHistoryStore _history;
    private readonly INebulaHttpFactory _http;
    private readonly AccountLinkService _linkService;
    private readonly LinkStore _links;
    private readonly object _lock = new();
    private readonly ServerAccountMirror _mirror;
    private readonly Action<NebulaLinkChange> _raise;
    private readonly AccountStateReporter _reporter;
    private volatile NebulaModuleCatalog _catalog = ModuleCatalogs.Empty;
    private NebulaConnection? _connection;
    private Timer? _incompatibleTimer;
    private DateTimeOffset _lastRealign = DateTimeOffset.MinValue;
    private int _unauthorizedCount;
    private volatile bool _unlinking;

    public LinkSession(LinkStore links, NebulaLinkHistoryStore history, DeviceCredentialManager credentials,
        ServerAccountMirror mirror, AccountLinkService linkService, AccountStateReporter reporter, INebulaHttpFactory http,
        Func<DateTimeOffset> clock, Action<NebulaLinkChange> raise)
    {
        _links = links;
        _history = history;
        _credentials = credentials;
        _mirror = mirror;
        _linkService = linkService;
        _reporter = reporter;
        _http = http;
        _clock = clock;
        _raise = raise;
    }

    /// <summary>Set after construction (the executor sends its results through this session).</summary>
    public NebulaRequestExecutor Executor { get; set; } = null!;

    public NebulaLinkState State { get; private set; } = NebulaLinkState.Disabled;

    /// <summary>Reason of the current state (revoke / link state reason).</summary>
    public string? StateReason { get; private set; }

    /// <summary>Incompatible: the server's major is newer (update NebulaAuth) or older (Achiefy does not support it yet).</summary>
    public bool ServerNewer { get; private set; }

    public NebulaModuleCatalog Catalog => _catalog;

    /// <summary>The current connection (null when none).</summary>
    public NebulaConnection? Connection
    {
        get
        {
            lock (_lock) return _connection;
        }
    }

    #region Start and lifecycle

    /// <summary>Starts the link stored in nebula.json (credential, connection) unless paused / incompatible.</summary>
    public void Start()
    {
        var data = _links.Snapshot();
        _catalog = data?.ModuleCatalog ?? ModuleCatalogs.Empty;
        if (data?.LinkId == null)
        {
            SetState(NebulaLinkState.Disabled);
            return;
        }

        _ = StartLinkAsync();
    }

    private async Task StartLinkAsync()
    {
        try
        {
            var data = _links.Snapshot();
            if (data?.LinkId is not { } linkId) return;
            if (HubUrlOf(data) == null)
            {
                NebulaLinkLog.Error("The stored Nebula Link address is missing or not allowed; unlink and pair again");
                SetState(NebulaLinkState.CredentialUnavailable);
                return;
            }

            if (await _credentials.LoadAsync(linkId, data.CredentialStore) == null)
            {
                SetState(NebulaLinkState.CredentialUnavailable);
                return;
            }

            if (data.Paused)
            {
                SetState(NebulaLinkState.Paused);
                return;
            }

            if (data.IncompatibleAt is { } at && _clock() - at < IncompatibleRetry)
            {
                ServerNewer = data.IncompatibleServerMajor > NebulaProtocol.Major;
                SetState(NebulaLinkState.Incompatible);
                ScheduleIncompatibleRetry(at + IncompatibleRetry - _clock());
                return;
            }

            Connect();
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Error("The Achiefy link can't be started", ex);
            SetState(NebulaLinkState.CredentialUnavailable);
        }
    }

    /// <summary>A pairing stored a new link (credential and nebula.json): nothing of an earlier link is kept.</summary>
    public void OnPaired()
    {
        _unauthorizedCount = 0;
        _mirror.Clear();
        _linkService.ClearFailures();
        _reporter.Reset();
        _history.ClearLinkData();
        _history.Persist = true;
        Connect();
    }

    /// <summary>Hub address of the stored link; null when the stored address is missing or not well-formed.</summary>
    private static Uri? HubUrlOf(NebulaLinkFile? data)
    {
        if (string.IsNullOrWhiteSpace(data?.TenantBaseUrl)) return null;
        if (!Uri.TryCreate(data.TenantBaseUrl.TrimEnd('/') + NebulaProtocol.HubPath, UriKind.Absolute, out var hub))
            return null;
        return NebulaLinkApis.IsWellFormed(hub) ? hub : null;
    }

    private void Connect()
    {
        var hub = HubUrlOf(_links.Snapshot());
        if (hub == null)
        {
            SetState(NebulaLinkState.CredentialUnavailable);
            return;
        }

        var events = new CurrentConnectionEvents(this);
        var connection = new NebulaConnection(hub, () => _credentials.Current, _http.CreateHandler, BuildHello, events);
        events.Connection = connection;
        NebulaConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = connection;
        }

        if (old != null) _ = old.StopAsync();
        SetState(NebulaLinkState.Connecting);
        connection.Start();
    }

    private HelloRequest BuildHello()
    {
        var data = _links.Snapshot();
        return new HelloRequest
        {
            ProtocolMajor = NebulaProtocol.Major,
            ProtocolMinor = NebulaProtocol.Minor,
            Capabilities = NebulaCapabilities.V1.ToArray(),
            AppVersion = LinkDeviceInfo.AppVersion,
            Os = LinkDeviceInfo.Os,
            OsVersion = LinkDeviceInfo.OsVersion,
            DeviceName = data?.DeviceName ?? LinkDeviceInfo.DefaultDeviceName,
            LinkedAccounts = data?.Accounts.Count ?? 0
        };
    }

    public async Task PauseAsync()
    {
        _links.Update(d => d.Paused = true);
        Executor.CancelAll(NebulaResultCodes.DevicePaused);
        var connection = Connection;
        if (connection is {IsConnected: true})
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await connection.InvokeAsync<AckResponse>("SetPaused", new SetPausedRequest {Paused = true}, cts.Token);
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("SetPaused failed; the cabinet shows the device offline until the next connect", ex);
            }
        }

        await StopConnectionAsync();
        SetState(NebulaLinkState.Paused);
    }

    public async Task ResumeAsync()
    {
        _links.Update(d => d.Paused = false);
        if (_credentials.Current == null)
        {
            await StartLinkAsync();
            return;
        }

        Connect();
    }

    /// <summary>"Use here" after another copy took the link, or retry after a refused credential.</summary>
    public Task ReconnectAsync()
    {
        _unauthorizedCount = 0;
        if (_credentials.Current == null) return StartLinkAsync();
        Connect();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Unlinks this device: tells the server when possible (≤ 5 s connected, one connect attempt ≤ 10 s otherwise),
    ///     then always deletes the local credential and the account links with their grants. A revoke the server sends
    ///     for this unlink meanwhile is ignored.
    /// </summary>
    /// <returns>True when the server confirmed the unlink.</returns>
    public async Task<bool> UnlinkAsync()
    {
        var data = _links.Snapshot();
        if (data?.LinkId is not { } linkId) return true;
        _unlinking = true;
        var acknowledged = false;
        try
        {
            Executor.CancelAll(NebulaResultCodes.NotPaired);
            try
            {
                var connection = Connection;
                // Not connected (offline, paused): one attempt to tell the server, then the local cleanup anyway
                if (connection is not {IsConnected: true} && _credentials.Current != null &&
                    State is not (NebulaLinkState.Replaced or NebulaLinkState.Incompatible))
                {
                    if (connection == null) Connect();
                    var deadline = _clock() + TimeSpan.FromSeconds(10);
                    while (_clock() < deadline && Connection is not {IsConnected: true}) await Task.Delay(200);
                    connection = Connection;
                }

                if (connection is {IsConnected: true})
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var ack = await connection.InvokeAsync<AckResponse>("Unlink", new UnlinkRequest {Reason = "user"}, cts.Token);
                    acknowledged = ack.Accepted;
                }
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("Unlink was not confirmed by the server", ex);
            }

            await StopConnectionAsync();
            await ForgetLinkAsync(linkId, data.CredentialStore);
            SetState(NebulaLinkState.Disabled);
        }
        finally
        {
            _unlinking = false;
        }

        NebulaLinkLog.Info($"Device unlinked (server confirmed: {acknowledged})");
        return acknowledged;
    }

    /// <summary>After a revoke: back to the pairing pages.</summary>
    public void AcknowledgeRevoked()
    {
        if (State is NebulaLinkState.Revoked) SetState(NebulaLinkState.Disabled);
    }

    private async Task HandleRevokedAsync(string? reason)
    {
        var data = _links.Snapshot();
        // This device is unlinking itself, or the link is already forgotten locally: nothing to do
        if (_unlinking || data?.LinkId is not { } linkId) return;
        Executor.CancelAll(NebulaResultCodes.NotPaired);
        Connection?.StopReconnecting();
        await StopConnectionAsync();
        await ForgetLinkAsync(linkId, data.CredentialStore);
        StateReason = reason;
        SetState(NebulaLinkState.Revoked);
    }

    private async Task ForgetLinkAsync(Guid linkId, string? storeKind)
    {
        foreach (var link in _links.Accounts()) _linkService.Forget(link.Login);
        await _credentials.DeleteAsync(linkId, storeKind);
        _credentials.Forget();
        // Nothing of nebula.json is needed without a link (the request history stays)
        _links.Delete();
        _history.ClearLinkData();
        _history.Flush();
        _history.Persist = false;
        _mirror.Clear();
        _linkService.ClearFailures();
        _reporter.Reset();
        _reporter.Stop();
        _raise(NebulaLinkChange.Accounts);
    }

    private async Task StopConnectionAsync()
    {
        NebulaConnection? connection;
        lock (_lock)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection != null) await connection.StopAsync();
    }

    /// <summary>App exit: closes the connection.</summary>
    public async Task ShutdownAsync()
    {
        _reporter.Stop();
        await StopConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>The app's storage mode changed: the credential moves to the store of the new mode.</summary>
    public async Task OnStorageModeChangedAsync(bool encryptionOff)
    {
        if (!await _credentials.OnStorageModeChangedAsync(encryptionOff))
        {
            // Kept in memory for this session only; the next start shows the credential as unavailable
            NebulaLinkLog.Warn("No secure store after the storage mode change: re-enable encryption or unlink");
            _raise(NebulaLinkChange.State);
        }
    }

    private void ScheduleIncompatibleRetry(TimeSpan due)
    {
        _incompatibleTimer?.Dispose();
        _incompatibleTimer = new Timer(_ =>
        {
            if (State != NebulaLinkState.Incompatible) return;
            _links.Update(d => d.IncompatibleAt = null);
            Connect();
        }, null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    #endregion

    #region Connection events

    private async Task OnHelloAcceptedAsync(NebulaConnection connection, HelloResponse hello, CancellationToken ct)
    {
        _unauthorizedCount = 0;
        _links.Update(d =>
        {
            d.TenantId = hello.TenantId;
            d.TenantLabel = hello.TenantLabel;
            d.AccountLabel = hello.AccountLabel;
            d.IncompatibleAt = null;
            d.IncompatibleServerMajor = null;
        });
        StateReason = hello.LinkStateReason;
        Executor.SetMaxInFlight(hello.Limits?.MaxInFlight ?? 0);
        if (hello.ModuleCatalog != null) SetCatalog(hello.ModuleCatalog);
        SetState(hello.LinkState == NebulaLinkStates.Suspended ? NebulaLinkState.Suspended : NebulaLinkState.Connected);

        if (hello.RotateCredential && hello.Capabilities.Contains(NebulaCapabilities.CredentialRotate))
            await _credentials.RotateAsync(connection, ct);
        await _mirror.SyncAsync(connection, null, ct);
        _raise(NebulaLinkChange.Accounts);
        await _reporter.ReportAllAsync(ct);
        await FlushEventsAsync();
        _ = RealignTimeIfNeeded();
    }

    private async Task RealignTimeIfNeeded()
    {
        if (_clock() - _lastRealign < TimeSpan.FromMinutes(30)) return;
        _lastRealign = _clock();
        try
        {
            await SteamLib.SteamMobile.TimeAligner.AlignTimeAsync();
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Steam time realignment failed", ex);
        }
    }

    private void OnHelloRejected(HelloResponse hello)
    {
        if (hello.Code == NebulaHubErrors.NotPaired)
        {
            _ = HandleRevokedAsync(NebulaLinkReasons.RevokedByUser);
            return;
        }

        ServerNewer = hello.ProtocolMajor > NebulaProtocol.Major;
        _links.Update(d =>
        {
            d.IncompatibleAt = _clock();
            d.IncompatibleServerMajor = hello.ProtocolMajor;
        });
        SetState(NebulaLinkState.Incompatible);
        ScheduleIncompatibleRetry(IncompatibleRetry);
    }

    /// <summary>The server's module catalog replaces the stored one (grants of entries no longer listed stay, unusable).</summary>
    private void SetCatalog(NebulaModuleCatalog catalog)
    {
        var clean = ModuleCatalogs.Sanitize(catalog);
        _catalog = clean;
        try
        {
            _links.Update(d => d.ModuleCatalog = clean);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("The module catalog can't be saved", ex);
        }

        NebulaLinkLog.Info($"Module catalog {clean.Version}: {clean.Modules.Length} module(s)");
        _raise(NebulaLinkChange.Accounts);
    }

    private void OnAccountsChanged(AccountsChangedMessage message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var connection = Connection;
                if (connection is not {IsConnected: true}) return;
                var ids = message.Full ? null : message.AccountIds;
                await _mirror.SyncAsync(connection, ids, CancellationToken.None);
                _raise(NebulaLinkChange.Accounts);
                await _reporter.ReportAsync(ids, CancellationToken.None);
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("Account sync after a server change failed", ex);
            }
        });
    }

    private void OnLinkStateChanged(LinkStateMessage message)
    {
        NebulaLinkLog.Info($"Link state from the server: {message.State} ({message.Reason})");
        switch (message.State)
        {
            case NebulaLinkStates.Revoked:
                _ = HandleRevokedAsync(message.Reason);
                break;
            case NebulaLinkStates.Replaced:
                Connection?.StopReconnecting();
                Executor.CancelAll(NebulaResultCodes.Cancelled);
                StateReason = message.Reason;
                SetState(NebulaLinkState.Replaced);
                break;
            case NebulaLinkStates.Suspended:
                StateReason = message.Reason;
                SetState(NebulaLinkState.Suspended);
                break;
            case NebulaLinkStates.Active:
                StateReason = null;
                if (Connection is {IsConnected: true}) SetState(NebulaLinkState.Connected);
                break;
        }
    }

    private void OnHeartbeat(HeartbeatResponse response)
    {
        // Steam time is realigned every 30 min while connected (login codes depend on it)
        _ = RealignTimeIfNeeded();
        if (response.LinkState == NebulaLinkStates.Suspended && State == NebulaLinkState.Connected)
            SetState(NebulaLinkState.Suspended);
        else if (response.LinkState == NebulaLinkStates.Active && State == NebulaLinkState.Suspended)
            SetState(NebulaLinkState.Connected);
    }

    private void OnConnectFailed(bool unauthorized)
    {
        if (!unauthorized)
        {
            _unauthorizedCount = 0;
            return;
        }

        if (++_unauthorizedCount < MAX_UNAUTHORIZED_ATTEMPTS) return;
        Connection?.StopReconnecting();
        SetState(NebulaLinkState.CredentialRejected);
    }

    private void OnConnectionStateChanged(NebulaConnectionState state)
    {
        if (State is NebulaLinkState.Paused or NebulaLinkState.Revoked or NebulaLinkState.Replaced or NebulaLinkState.Incompatible
            or NebulaLinkState.CredentialRejected or NebulaLinkState.Disabled) return;
        switch (state)
        {
            case NebulaConnectionState.Connecting:
                SetState(NebulaLinkState.Connecting);
                break;
            case NebulaConnectionState.Reconnecting:
                SetState(NebulaLinkState.Offline);
                break;
            case NebulaConnectionState.Stopped when State is NebulaLinkState.Connected or NebulaLinkState.Suspended
                or NebulaLinkState.Connecting:
                SetState(NebulaLinkState.Offline);
                break;
        }
    }

    /// <summary>The result of an Achiefy request.</summary>
    public async Task SendResultAsync(NebulaCommandResult result)
    {
        var connection = Connection;
        if (connection is not {IsConnected: true}) throw new InvalidOperationException("Not connected");
        var ack = await connection.InvokeAsync<AckResponse>("CompleteRequest", result);
        if (!ack.Accepted) NebulaLinkLog.Info($"Result of request {result.RequestId} not accepted: {ack.Code}");
    }

    /// <summary>Sends queued user-action events (manual decisions on operations Achiefy asked about).</summary>
    public async Task FlushEventsAsync()
    {
        var connection = Connection;
        if (connection is not {IsConnected: true}) return;
        if (connection.Hello?.Capabilities.Contains(NebulaCapabilities.EventUserAction) == false) return;
        foreach (var evt in _history.PendingEvents())
        {
            try
            {
                var ack = await connection.InvokeAsync<AckResponse>("ReportUserAction", evt);
                _history.RemoveEvent(evt.EventId);
                if (!ack.Accepted) NebulaLinkLog.Info($"User action event {evt.EventId} not accepted: {ack.Code}");
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("User action event not delivered now", ex);
                return;
            }
        }
    }

    #endregion

    private void SetState(NebulaLinkState state)
    {
        if (State == state) return;
        State = state;
        NebulaLinkLog.Info("State: " + state.ToString().ToLower(CultureInfo.InvariantCulture));
        _raise(NebulaLinkChange.State);
    }

    /// <summary>Forwards the events of one connection while it is the current one (a replaced connection is ignored).</summary>
    private sealed class CurrentConnectionEvents(LinkSession session) : INebulaConnectionHandler
    {
        public NebulaConnection? Connection { get; set; }

        private bool IsCurrent => Connection != null && ReferenceEquals(session.Connection, Connection);

        public Task OnHelloAcceptedAsync(NebulaConnection connection, HelloResponse hello, CancellationToken ct)
        {
            return IsCurrent ? session.OnHelloAcceptedAsync(connection, hello, ct) : Task.CompletedTask;
        }

        public void OnHelloRejected(HelloResponse hello)
        {
            if (IsCurrent) session.OnHelloRejected(hello);
        }

        public void OnExecute(NebulaCommand command)
        {
            if (IsCurrent) session.Executor.Execute(command);
        }

        public void OnCancelCommand(CancelCommandMessage message)
        {
            if (IsCurrent) session.Executor.Cancel(message.RequestId, message.Reason);
        }

        public void OnAccountsChanged(AccountsChangedMessage message)
        {
            if (IsCurrent) session.OnAccountsChanged(message);
        }

        public void OnLinkStateChanged(LinkStateMessage message)
        {
            if (IsCurrent) session.OnLinkStateChanged(message);
        }

        public void OnModuleCatalogChanged(NebulaModuleCatalog catalog)
        {
            if (IsCurrent) session.SetCatalog(catalog);
        }

        public void OnHeartbeat(HeartbeatResponse response)
        {
            if (IsCurrent) session.OnHeartbeat(response);
        }

        public void OnConnectFailed(Exception error, bool unauthorized)
        {
            if (IsCurrent) session.OnConnectFailed(unauthorized);
        }

        public void OnStateChanged(NebulaConnectionState state)
        {
            if (IsCurrent) session.OnConnectionStateChanged(state);
        }
    }
}
