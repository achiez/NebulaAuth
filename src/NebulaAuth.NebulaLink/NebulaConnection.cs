using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NebulaAuth.NebulaLink.Protocol;
using NLog.Extensions.Logging;

namespace NebulaAuth.NebulaLink;

public enum NebulaConnectionState
{
    Stopped,
    Connecting,
    Connected,

    /// <summary>Disconnected; the next attempt is scheduled (backoff).</summary>
    Reconnecting
}

/// <summary>What the owner of a <see cref="NebulaConnection" /> handles (hub messages and connection events).</summary>
public interface INebulaConnectionHandler
{
    /// <summary>After an accepted Hello: rotation, account sync, state report, queued events.</summary>
    Task OnHelloAcceptedAsync(NebulaConnection connection, HelloResponse hello, CancellationToken ct);

    /// <summary>Hello refused (incompatible_version / not_paired): the connection stops reconnecting.</summary>
    void OnHelloRejected(HelloResponse hello);

    void OnExecute(NebulaCommand command);
    void OnCancelCommand(CancelCommandMessage message);
    void OnAccountsChanged(AccountsChangedMessage message);
    void OnLinkStateChanged(LinkStateMessage message);

    /// <summary>The server module catalog changed (full catalog).</summary>
    void OnModuleCatalogChanged(NebulaModuleCatalog catalog);
    void OnHeartbeat(HeartbeatResponse response);

    /// <summary>A connect attempt failed; <paramref name="unauthorized" />: the server rejected the credential (401/403).</summary>
    void OnConnectFailed(Exception error, bool unauthorized);

    void OnStateChanged(NebulaConnectionState state);
}

/// <summary>
///     The single outgoing SignalR connection to the Achiefy device hub: WebSockets only, negotiation skipped, the device
///     credential only in the Authorization header, Newtonsoft JSON protocol, redirects never followed, default TLS
///     validation. Hello with version negotiation on every connect, a heartbeat, and its own reconnect loop with capped
///     exponential backoff and jitter (0, 2, 5, 10, 20, 30, 60 s, then 60 s). Handlers are invoked on the thread pool and
///     never block the connection (a hub call awaited inside a SignalR handler would deadlock).
/// </summary>
public sealed class NebulaConnection : IAsyncDisposable
{
    private static readonly int[] BackoffSeconds = [0, 2, 5, 10, 20, 30, 60];

    private readonly Func<string?> _credential;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly INebulaConnectionHandler _handler;
    private readonly Func<HelloRequest> _hello;
    private readonly Uri _hubUrl;
    private readonly Lock _lock = new();
    private readonly Random _random = new();
    private HubConnection? _current;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _stopReconnect;

    public NebulaConnection(Uri hubUrl, Func<string?> credential, Func<HttpMessageHandler> handlerFactory,
        Func<HelloRequest> hello, INebulaConnectionHandler handler)
    {
        if (!NebulaLinkApis.IsWellFormed(hubUrl))
            throw new InvalidOperationException("The Nebula Link hub address is not allowed");
        _hubUrl = hubUrl;
        _credential = credential;
        _handlerFactory = handlerFactory;
        _hello = hello;
        _handler = handler;
    }

    public NebulaConnectionState State { get; private set; } = NebulaConnectionState.Stopped;

    /// <summary>The negotiated Hello of the current connection (null while not connected).</summary>
    public HelloResponse? Hello { get; private set; }

    public bool IsConnected => State == NebulaConnectionState.Connected && _current != null;

    /// <summary>Timeout of every hub call.</summary>
    public TimeSpan InvokeTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Starts the connect / reconnect loop (no-op when running).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_loop is {IsCompleted: false}) return;
            _stopReconnect = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>The current connection is closed and no reconnect follows (paused, revoked, replaced, incompatible).</summary>
    public void StopReconnecting()
    {
        _stopReconnect = true;
    }

    /// <summary>Stops the loop and closes the connection.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_lock)
        {
            _stopReconnect = true;
            _cts?.Cancel();
            loop = _loop;
        }

        var connection = _current;
        if (connection != null)
        {
            try
            {
                await connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // closing anyway
            }
        }

        if (loop != null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // the loop ends on its own
            }
        }

        SetState(NebulaConnectionState.Stopped);
    }

    /// <summary>Calls a hub method on the current connection.</summary>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="HubException">Protocol-level failure (<see cref="NebulaHubErrors" />).</exception>
    public async Task<T> InvokeAsync<T>(string method, object? arg, CancellationToken ct = default)
    {
        var connection = _current ?? throw new InvalidOperationException("Not connected to Achiefy");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InvokeTimeout);
        return arg == null
            ? await connection.InvokeAsync<T>(method, timeout.Token)
            : await connection.InvokeAsync<T>(method, arg, timeout.Token);
    }

    /// <summary>Maps a hub error to a protocol code (unknown messages: internal_error).</summary>
    public static string HubErrorCode(Exception ex)
    {
        if (ex is not HubException hub) return NebulaResultCodes.InternalError;
        var message = hub.Message;
        foreach (var code in new[]
                 {
                     NebulaHubErrors.HelloRequired, NebulaHubErrors.StaleConnection, NebulaHubErrors.IncompatibleVersion,
                     NebulaHubErrors.NotPaired, NebulaHubErrors.InvalidRequest
                 })
        {
            if (message == code || message.EndsWith(": " + code, StringComparison.Ordinal) ||
                message.EndsWith(code, StringComparison.Ordinal))
                return code;
        }

        return NebulaResultCodes.InternalError;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        var wasConnected = false;
        while (!ct.IsCancellationRequested && !_stopReconnect)
        {
            var delay = Backoff(attempt);
            if (delay > TimeSpan.Zero)
            {
                SetState(NebulaConnectionState.Reconnecting);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (ct.IsCancellationRequested || _stopReconnect) break;
            SetState(attempt == 0 && !wasConnected
                ? NebulaConnectionState.Connecting
                : NebulaConnectionState.Reconnecting);
            attempt++;
            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            HubConnection connection;
            try
            {
                connection = Build(closed);
                await connection.StartAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var unauthorized = IsUnauthorized(ex);
                NebulaLinkLog.Warn($"Connect to Achiefy failed (attempt {attempt}{(unauthorized ? ", unauthorized" : "")})",
                    ex);
                SafeCall(() => _handler.OnConnectFailed(ex, unauthorized));
                continue;
            }

            HelloResponse hello;
            try
            {
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                helloTimeout.CancelAfter(InvokeTimeout);
                hello = await connection.InvokeAsync<HelloResponse>("Hello", _hello(), helloTimeout.Token);
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn($"Hello failed: {HubErrorCode(ex)}", ex);
                await SafeDispose(connection);
                if (ct.IsCancellationRequested) break;
                if (HubErrorCode(ex) is NebulaHubErrors.NotPaired or NebulaHubErrors.IncompatibleVersion)
                {
                    SafeCall(() => _handler.OnHelloRejected(new HelloResponse {Accepted = false, Code = HubErrorCode(ex)}));
                    break;
                }

                continue;
            }

            if (!hello.Accepted)
            {
                NebulaLinkLog.Warn($"Hello refused: {hello.Code} (server protocol {hello.ProtocolMajor}.{hello.ProtocolMinor})");
                await SafeDispose(connection);
                SafeCall(() => _handler.OnHelloRejected(hello));
                break;
            }

            NebulaLinkLog.Info(
                $"Connected to Achiefy: protocol {hello.ProtocolMajor}.{hello.ProtocolMinor}, link state {hello.LinkState}");
            Hello = hello;
            _current = connection;
            attempt = 0;
            SetState(NebulaConnectionState.Connected);
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatLoop(hello.HeartbeatIntervalMs, connectionCts.Token);
            try
            {
                await _handler.OnHelloAcceptedAsync(this, hello, connectionCts.Token);
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("Post-hello synchronization failed", ex);
            }

            Exception? closeError;
            try
            {
                closeError = await closed.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                closeError = null;
            }

            connectionCts.Cancel();
            try
            {
                await heartbeat;
            }
            catch
            {
                // ended
            }

            _current = null;
            Hello = null;
            await SafeDispose(connection);
            if (closeError != null) NebulaLinkLog.Warn("Achiefy connection closed", closeError);
            else NebulaLinkLog.Info("Achiefy connection closed");
            // The first reconnect is immediate, then the backoff sequence
            attempt = 0;
            wasConnected = true;
            if (!_stopReconnect && !ct.IsCancellationRequested) SetState(NebulaConnectionState.Reconnecting);
        }

        SetState(NebulaConnectionState.Stopped);
    }

    private HubConnection Build(TaskCompletionSource<Exception?> closed)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl, HttpTransportType.WebSockets, o =>
            {
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
                o.AccessTokenProvider = () => Task.FromResult(_credential());
                o.HttpMessageHandlerFactory = _ => _handlerFactory();
            })
            .AddNewtonsoftJsonProtocol()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                // Never Trace/Debug: those levels log message payloads
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddProvider(new NLogLoggerProvider());
            })
            .Build();
        connection.ServerTimeout = TimeSpan.FromSeconds(45);
        connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        connection.HandshakeTimeout = TimeSpan.FromSeconds(15);
        connection.On<NebulaCommand>("Execute", cmd => SafeCall(() => _handler.OnExecute(cmd)));
        connection.On<CancelCommandMessage>("CancelCommand", m => SafeCall(() => _handler.OnCancelCommand(m)));
        connection.On<AccountsChangedMessage>("AccountsChanged", m => SafeCall(() => _handler.OnAccountsChanged(m)));
        connection.On<LinkStateMessage>("LinkStateChanged", m => SafeCall(() => _handler.OnLinkStateChanged(m)));
        connection.On<NebulaModuleCatalog>("ModuleCatalogChanged", c => SafeCall(() => _handler.OnModuleCatalogChanged(c)));
        connection.Closed += ex =>
        {
            closed.TrySetResult(ex);
            return Task.CompletedTask;
        };
        return connection;
    }

    private async Task HeartbeatLoop(int intervalMs, CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(intervalMs is >= 5000 and <= 300000 ? intervalMs : 30000);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            try
            {
                var response = await InvokeAsync<HeartbeatResponse>("Heartbeat", null, ct);
                SafeCall(() => _handler.OnHeartbeat(response));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn($"Heartbeat failed: {HubErrorCode(ex)}", ex);
                if (HubErrorCode(ex) == NebulaHubErrors.StaleConnection)
                {
                    // Another connection took over: this one can't be used any more
                    var connection = _current;
                    if (connection != null) _ = connection.StopAsync();
                    return;
                }
            }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        if (attempt <= 0) return TimeSpan.Zero;
        var seconds = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
        double jitter;
        lock (_random) jitter = _random.NextDouble() * 0.2 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static bool IsUnauthorized(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException {StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden})
                return true;
            var message = e.Message;
            if (message.Contains("'401'", StringComparison.Ordinal) || message.Contains("'403'", StringComparison.Ordinal) ||
                message.Contains("401 (Unauthorized)", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void SetState(NebulaConnectionState state)
    {
        if (State == state) return;
        State = state;
        SafeCall(() => _handler.OnStateChanged(state));
    }

    private static void SafeCall(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Error("Achiefy handler failed", ex);
        }
    }

    private static async Task SafeDispose(HubConnection connection)
    {
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // disposed anyway
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
