using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaAuth.Model.Browser;

/// <summary>
///     Per-session HTTP proxy on 127.0.0.1:&lt;random port&gt; that the embedded browser uses. Every connection is
///     tunnelled through the account proxy (<see cref="UpstreamConnector" />); an upstream failure is answered with
///     <c>502</c> and there is no direct fallback. With local credentials the browser has to present them (answered by the
///     app only for the <see cref="REALM" /> challenge), so other local processes can't use the tunnel.
/// </summary>
public sealed class LocalForwarder : IAsyncDisposable
{
    public const string REALM = "nebula-local";
    public const string ERROR_HEADER = "X-Nebula-Proxy-Error";

    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly string? _localAuthHeader;
    private readonly UpstreamProxy _upstream;
    private Task? _acceptLoop;
    private int _failed;

    private LocalForwarder(UpstreamProxy upstream, bool withLocalCredentials)
    {
        _upstream = upstream;
        if (withLocalCredentials)
        {
            LocalUser = "nb" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            LocalPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _localAuthHeader = "Basic " +
                               Convert.ToBase64String(Encoding.UTF8.GetBytes($"{LocalUser}:{LocalPassword}"));
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public int Port => ((IPEndPoint) _listener.LocalEndpoint).Port;
    public string ProxyServer => $"http://127.0.0.1:{Port}";
    public string? LocalUser { get; }
    public string? LocalPassword { get; }

    /// <summary>Number of connections the upstream proxy failed (answered with 502).</summary>
    public int FailedCount => Volatile.Read(ref _failed);

    public UpstreamFailure? LastFailure { get; private set; }

    public static LocalForwarder Start(UpstreamProxy upstream, bool withLocalCredentials)
    {
        var f = new LocalForwarder(upstream, withLocalCredentials);
        f._listener.Start(128);
        f._acceptLoop = Task.Run(f.AcceptLoopAsync);
        return f;
    }

    /// <summary>
    ///     Opens a tunnel to <paramref name="host" />:<paramref name="port" /> through this forwarder (the same path the
    ///     browser uses) and closes it. Null on success, else the upstream failure.
    /// </summary>
    public async Task<UpstreamFailure?> ProbeAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient {NoDelay = true};
        await tcp.ConnectAsync(IPAddress.Loopback, Port, ct);
        var s = tcp.GetStream();
        var sb = new StringBuilder($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n");
        if (_localAuthHeader != null) sb.Append("Proxy-Authorization: ").Append(_localAuthHeader).Append("\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        var head = await UpstreamConnector.ReadHeadAsync(s, ct);
        var lines = head.Split("\r\n");
        if (lines[0].Split(' ') is [_, "200", ..]) return null;
        var error = lines.FirstOrDefault(l => l.StartsWith(ERROR_HEADER + ":", StringComparison.OrdinalIgnoreCase));
        return error != null && Enum.TryParse<UpstreamFailure>(error[(ERROR_HEADER.Length + 1)..].Trim(), out var f)
            ? f
            : UpstreamFailure.Protocol;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try
            {
                c = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(c));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var cs = client.GetStream();
        var ct = _cts.Token;
        try
        {
            var head = await UpstreamConnector.ReadHeadAsync(cs, ct);
            var lines = head.Split("\r\n");
            var rl = lines[0].Split(' ');
            if (rl.Length < 3) return;
            var method = rl[0];

            if (_localAuthHeader != null)
            {
                var pa = lines.FirstOrDefault(l =>
                    l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                if (pa == null || !FixedTimeEquals(pa["Proxy-Authorization:".Length..].Trim(), _localAuthHeader))
                {
                    await WriteAsync(cs,
                        $"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"{REALM}\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        ct);
                    return;
                }
            }

            string host;
            int port;
            byte[]? firstBytes = null;
            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySplitHostPort(rl[1], out host, out port))
                {
                    await WriteAsync(cs, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        ct);
                    return;
                }
            }
            else
            {
                // Plain http:// in absolute form: re-sent to the origin through the tunnel without Proxy-* headers
                if (!Uri.TryCreate(rl[1], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
                {
                    await WriteAsync(cs, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        ct);
                    return;
                }

                host = uri.IdnHost;
                port = uri.Port;
                var isUpgrade = lines.Any(l => l.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));
                var sb = new StringBuilder($"{method} {uri.PathAndQuery} {rl[2]}\r\n");
                foreach (var l in lines.Skip(1))
                {
                    if (l.Length == 0) continue;
                    if (l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!isUpgrade && l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(l).Append("\r\n");
                }

                if (!isUpgrade) sb.Append("Connection: close\r\n");
                sb.Append("\r\n");
                firstBytes = Encoding.Latin1.GetBytes(sb.ToString());
            }

            Stream up;
            try
            {
                up = await UpstreamConnector.OpenTunnelAsync(_upstream, host, port, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var failure = ex is UpstreamException ue ? ue.Failure : UpstreamFailure.Unreachable;
                Interlocked.Increment(ref _failed);
                LastFailure = failure;
                // Target and failure only: the proxy credentials are never logged
                Shell.Logger.Debug("Browser proxy: tunnel to {target} failed ({failure})", $"{host}:{port}", failure);
                await WriteAsync(cs,
                    $"HTTP/1.1 502 Bad Gateway\r\n{ERROR_HEADER}: {failure}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    ct);
                return;
            }

            await using (up)
            {
                if (firstBytes == null)
                    await WriteAsync(cs, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);
                else
                    await up.WriteAsync(firstBytes, ct);

                var a = cs.CopyToAsync(up, ct);
                var b = up.CopyToAsync(cs, ct);
                await Task.WhenAny(a, b);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException
                                       or ObjectDisposedException)
        {
            // Connection closed by one of the sides or the forwarder stopped
        }
        catch (Exception ex)
        {
            Shell.Logger.Debug(ex, "Browser proxy: connection error");
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    private static bool TrySplitHostPort(string s, out string host, out int port)
    {
        host = "";
        port = 0;
        if (s.StartsWith('['))
        {
            var e = s.IndexOf(']');
            if (e < 0 || e + 2 > s.Length || s[e + 1] != ':') return false;
            host = s[1..e];
            return int.TryParse(s[(e + 2)..], out port) && port is > 0 and < 65536;
        }

        var i = s.LastIndexOf(':');
        if (i <= 0) return false;
        host = s[..i];
        return int.TryParse(s[(i + 1)..], out port) && port is > 0 and < 65536;
    }

    private static Task WriteAsync(Stream s, string text, CancellationToken ct)
    {
        return s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // stopped
            }
        }
        // _cts is not disposed: connection handlers still running read its (cancelled) token
    }
}
