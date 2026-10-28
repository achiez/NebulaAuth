using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Proxy;

namespace NebulaAuth.Model.Browser;

public enum UpstreamKind
{
    Http,
    Https,
    Socks4,
    Socks5
}

/// <summary>
///     Immutable copy of the proxy an embedded browser session was opened with (changing the account proxy later does
///     not affect the open session). <see cref="ToString" /> never contains the credentials.
/// </summary>
public sealed class UpstreamProxy
{
    public UpstreamProxy(UpstreamKind kind, string host, int port, string? user, string? password)
    {
        Kind = kind;
        Host = host;
        Port = port;
        User = user;
        Password = password;
    }

    public UpstreamKind Kind { get; }
    public string Host { get; }
    public int Port { get; }
    public string? User { get; }
    public string? Password { get; }
    public bool HasAuth => !string.IsNullOrEmpty(User);

    public static UpstreamProxy FromProxyData(ProxyData data)
    {
        var kind = data.Protocol switch
        {
            ProxyProtocol.HTTP => UpstreamKind.Http,
            ProxyProtocol.HTTPs => UpstreamKind.Https,
            ProxyProtocol.SOCKS4 => UpstreamKind.Socks4,
            ProxyProtocol.SOCKS5 => UpstreamKind.Socks5,
            _ => throw new NotSupportedException("Unknown proxy protocol " + data.Protocol)
        };
        return new UpstreamProxy(kind, data.Address, data.Port,
            data.AuthEnabled ? data.Username : null, data.AuthEnabled ? data.Password : null);
    }

    public ProxyData ToProxyData()
    {
        var protocol = Kind switch
        {
            UpstreamKind.Http => ProxyProtocol.HTTP,
            UpstreamKind.Https => ProxyProtocol.HTTPs,
            UpstreamKind.Socks4 => ProxyProtocol.SOCKS4,
            _ => ProxyProtocol.SOCKS5
        };
        return new ProxyData(protocol, Host, Port, User!, Password!);
    }

    public override string ToString()
    {
        return $"{Kind.ToString().ToLowerInvariant()}://{(HasAuth ? "***@" : "")}{Host}:{Port}";
    }
}

public enum UpstreamFailure
{
    /// <summary>The proxy server itself can't be reached (DNS, refused, network).</summary>
    Unreachable,

    /// <summary>The proxy rejected the credentials (HTTP 407, SOCKS5 auth failure) or requires credentials.</summary>
    AuthFailed,

    /// <summary>The proxy answered but refused the tunnel to the target.</summary>
    Refused,

    /// <summary>No answer in time.</summary>
    Timeout,

    /// <summary>The proxy speaks a different protocol or answered garbage.</summary>
    Protocol
}

public sealed class UpstreamException(UpstreamFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpstreamFailure Failure { get; } = failure;
}

/// <summary>
///     Opens a raw TCP tunnel to (host, port) through the upstream proxy (HTTP CONNECT, HTTPS CONNECT, SOCKS4a, SOCKS5 with
///     user/password). Host names are always sent to the proxy (remote DNS). There is no direct connection path.
/// </summary>
public static class UpstreamConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static async Task<Stream> OpenTunnelAsync(UpstreamProxy up, string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        var token = timeout.Token;
        var tcp = new TcpClient {NoDelay = true};
        try
        {
            try
            {
                await tcp.ConnectAsync(up.Host, up.Port, token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpstreamException(UpstreamFailure.Timeout, "Proxy connection timed out");
            }
            catch (SocketException ex)
            {
                throw new UpstreamException(UpstreamFailure.Unreachable, "Proxy unreachable: " + ex.SocketErrorCode, ex);
            }

            Stream s = tcp.GetStream();
            try
            {
                switch (up.Kind)
                {
                    case UpstreamKind.Https:
                        var ssl = new SslStream(s, false);
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {TargetHost = up.Host},
                            token);
                        s = ssl;
                        await HttpConnectAsync(s, up, host, port, token);
                        break;
                    case UpstreamKind.Http:
                        await HttpConnectAsync(s, up, host, port, token);
                        break;
                    case UpstreamKind.Socks5:
                        await Socks5Async(s, up, host, port, token);
                        break;
                    case UpstreamKind.Socks4:
                        await Socks4aAsync(s, up, host, port, token);
                        break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpstreamException(UpstreamFailure.Timeout, "Proxy handshake timed out");
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                throw new UpstreamException(UpstreamFailure.Protocol, "Proxy handshake failed: " + ex.Message, ex);
            }

            return s;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task HttpConnectAsync(Stream s, UpstreamProxy up, string host, int port, CancellationToken ct)
    {
        var hp = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        var sb = new StringBuilder($"CONNECT {hp} HTTP/1.1\r\nHost: {hp}\r\n");
        if (up.HasAuth)
        {
            sb.Append("Proxy-Authorization: Basic ")
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{up.User}:{up.Password}")))
                .Append("\r\n");
        }

        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        var head = await ReadHeadAsync(s, ct);
        var statusLine = head.Split("\r\n")[0];
        var parts = statusLine.Split(' ');
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal))
            throw new UpstreamException(UpstreamFailure.Protocol, "Not an HTTP proxy answer");
        switch (parts[1])
        {
            case "200":
                return;
            case "407":
                throw new UpstreamException(UpstreamFailure.AuthFailed, "Proxy authentication failed (407)");
            default:
                throw new UpstreamException(UpstreamFailure.Refused, "Proxy refused the tunnel: " + parts[1]);
        }
    }

    private static async Task Socks5Async(Stream s, UpstreamProxy up, string host, int port, CancellationToken ct)
    {
        await s.WriteAsync(up.HasAuth ? new byte[] {5, 2, 0, 2} : new byte[] {5, 1, 0}, ct);
        var r = await ReadExactAsync(s, 2, ct);
        if (r[0] != 5) throw new UpstreamException(UpstreamFailure.Protocol, "Not a SOCKS5 proxy");
        if (r[1] == 2)
        {
            if (!up.HasAuth)
                throw new UpstreamException(UpstreamFailure.AuthFailed, "SOCKS5 proxy requires credentials");
            var u = Encoding.UTF8.GetBytes(up.User!);
            var p = Encoding.UTF8.GetBytes(up.Password ?? "");
            if (u.Length > 255 || p.Length > 255)
                throw new UpstreamException(UpstreamFailure.AuthFailed, "SOCKS5 credentials are too long");
            var m = new byte[3 + u.Length + p.Length];
            m[0] = 1;
            m[1] = (byte) u.Length;
            u.CopyTo(m, 2);
            m[2 + u.Length] = (byte) p.Length;
            p.CopyTo(m, 3 + u.Length);
            await s.WriteAsync(m, ct);
            var ar = await ReadExactAsync(s, 2, ct);
            if (ar[1] != 0) throw new UpstreamException(UpstreamFailure.AuthFailed, "SOCKS5 authentication failed");
        }
        else if (r[1] == 0xFF)
        {
            throw new UpstreamException(UpstreamFailure.AuthFailed, "SOCKS5 proxy accepts no offered auth method");
        }
        else if (r[1] != 0)
        {
            throw new UpstreamException(UpstreamFailure.Protocol, "SOCKS5 proxy chose an unsupported auth method");
        }

        // The host name goes to the proxy (remote DNS): no local DNS lookup of the target
        byte[] req;
        if (IPAddress.TryParse(host, out var ip))
        {
            var ab = ip.GetAddressBytes();
            req = new byte[4 + ab.Length + 2];
            req[3] = (byte) (ab.Length == 4 ? 1 : 4);
            ab.CopyTo(req, 4);
        }
        else
        {
            var hb = Encoding.ASCII.GetBytes(host);
            req = new byte[5 + hb.Length + 2];
            req[3] = 3;
            req[4] = (byte) hb.Length;
            hb.CopyTo(req, 5);
        }

        req[0] = 5;
        req[1] = 1;
        req[2] = 0;
        req[^2] = (byte) (port >> 8);
        req[^1] = (byte) port;
        await s.WriteAsync(req, ct);
        var rep = await ReadExactAsync(s, 4, ct);
        if (rep[1] != 0)
            throw new UpstreamException(UpstreamFailure.Refused, "SOCKS5 proxy refused the tunnel: code " + rep[1]);
        var skip = rep[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadExactAsync(s, 1, ct))[0],
            _ => throw new UpstreamException(UpstreamFailure.Protocol, "Bad SOCKS5 reply")
        };
        await ReadExactAsync(s, skip + 2, ct);
    }

    private static async Task Socks4aAsync(Stream s, UpstreamProxy up, string host, int port, CancellationToken ct)
    {
        var uid = Encoding.ASCII.GetBytes(up.User ?? "");
        var buf = new List<byte> {4, 1, (byte) (port >> 8), (byte) port};
        var isIp = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        buf.AddRange(isIp ? ip!.GetAddressBytes() : [0, 0, 0, 1]);
        buf.AddRange(uid);
        buf.Add(0);
        if (!isIp)
        {
            buf.AddRange(Encoding.ASCII.GetBytes(host));
            buf.Add(0);
        }

        await s.WriteAsync(buf.ToArray(), ct);
        var rep = await ReadExactAsync(s, 8, ct);
        if (rep[1] == 0x5A) return;
        throw rep[1] is 0x5C or 0x5D
            ? new UpstreamException(UpstreamFailure.AuthFailed, $"SOCKS4 proxy rejected the user id ({rep[1]:X2})")
            : new UpstreamException(UpstreamFailure.Refused, $"SOCKS4 proxy refused the tunnel ({rep[1]:X2})");
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int n, CancellationToken ct)
    {
        var b = new byte[n];
        await s.ReadExactlyAsync(b, ct);
        return b;
    }

    /// <summary>Reads an HTTP head (up to CRLFCRLF) byte by byte, so nothing after the head is consumed.</summary>
    public static async Task<string> ReadHeadAsync(Stream s, CancellationToken ct, int max = 64 * 1024)
    {
        var buf = new List<byte>(512);
        var one = new byte[1];
        while (buf.Count < max)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) throw new IOException("Connection closed while reading the HTTP head");
            buf.Add(one[0]);
            var c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n')
                return Encoding.Latin1.GetString(buf.ToArray());
        }

        throw new IOException("HTTP head too large");
    }
}
