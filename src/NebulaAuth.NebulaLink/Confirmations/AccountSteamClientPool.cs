using System;
using System.Collections.Concurrent;
using System.Net.Http;
using AchiesUtilities.Web.Models;
using AchiesUtilities.Web.Proxy;
using SteamLib;
using SteamLib.Authentication;
using SteamLib.Web;
using SteamLibForked.Models.Session;

namespace NebulaAuth.NebulaLink.Confirmations;

/// <summary>
///     One Steam mobile client per account (own HttpClient, cookies and proxy), used by the confirmation service for the
///     manual screen and for Nebula Link requests. Proxy = the account's proxy, else the default proxy, else direct. A
///     configured proxy is never bypassed: its failure is an error, not a direct retry. A client is rebuilt when the
///     proxy of the account changes; its cookies follow the account's session.
/// </summary>
public sealed class AccountSteamClientPool : IDisposable
{
    private readonly ConcurrentDictionary<string, AccountSteamClient> _clients = new();
    private readonly Func<MobileDataExtended, ProxyData?> _proxyOf;

    /// <param name="proxyOf">The proxy an account uses (the app: its own proxy, else the default one); null: direct.</param>
    public AccountSteamClientPool(Func<MobileDataExtended, ProxyData?>? proxyOf = null)
    {
        _proxyOf = proxyOf ?? (_ => null);
    }

    /// <summary>Raised when the proxy of an account changed and its client was rebuilt (key = account key).</summary>
    public event Action<string>? ProxyChanged;

    /// <summary>The account key: the normalized login (the same key as the account links).</summary>
    public static string KeyOf(MobileDataExtended mafile)
    {
        return AccountMatcher.Normalize(mafile.AccountName);
    }

    public ProxyData? ProxyOf(MobileDataExtended mafile)
    {
        return _proxyOf(mafile);
    }

    public AccountSteamClient Get(MobileDataExtended mafile)
    {
        var key = KeyOf(mafile);
        var proxy = ProxyOf(mafile);
        while (true)
        {
            var client = _clients.GetOrAdd(key, _ => new AccountSteamClient(mafile, proxy));
            if (Equals(client.ProxyData, proxy))
            {
                client.SyncSession(mafile.SessionData);
                return client;
            }

            // The proxy changed: a new client (new connections through the new proxy only)
            if (_clients.TryRemove(new(key, client)))
            {
                client.Dispose();
                ProxyChanged?.Invoke(key);
            }
        }
    }

    public void Remove(string key)
    {
        if (_clients.TryRemove(key, out var client)) client.Dispose();
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }
}

public sealed class AccountSteamClient : IDisposable
{
    private readonly object _lock = new();
    private MobileSessionData? _appliedSession;
    private string? _appliedToken;

    public AccountSteamClient(MobileDataExtended mafile, ProxyData? proxy)
    {
        ProxyData = proxy;
        Proxy = new DynamicProxy();
        Proxy.SetData(proxy);
        var pair = ClientBuilder.BuildMobileClient(Proxy, null);
        Client = pair.Client;
        Handler = pair.Handler;
        SyncSession(mafile.SessionData);
    }

    public HttpClient Client { get; }
    public HttpClientHandler Handler { get; }
    private DynamicProxy Proxy { get; }
    public ProxyData? ProxyData { get; }
    public bool ProxyConfigured => ProxyData != null;
    public string ProxyKey => ProxyData == null ? "direct" : $"{ProxyData.Address}:{ProxyData.Port}";

    public HttpClientHandlerPair Chp => new(Client, Handler);

    /// <summary>Applies the account's session cookies when the session (or its mobile token) changed.</summary>
    public void SyncSession(MobileSessionData? session)
    {
        lock (_lock)
        {
            var token = session?.GetMobileToken()?.Token;
            if (ReferenceEquals(session, _appliedSession) && token == _appliedToken) return;
            Handler.CookieContainer.ClearAllCookies();
            if (session != null) Handler.CookieContainer.SetSteamMobileCookies(session);
            else Handler.CookieContainer.ClearMobileSessionCookies();
            _appliedSession = session;
            _appliedToken = token;
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Handler.Dispose();
    }
}
