using System;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Local stands: loopback addresses (any port and path) and the full base of <c>NEBULA_LINK_TENANT_URL</c> can be
///     paired with; <c>NEBULA_LINK_ALLOW_UNTRUSTED=1</c> accepts any http(s) address and only logs an <c>apiOrigin</c>
///     mismatch.
/// </summary>
public static partial class NebulaLinkApis
{
    private static readonly bool Untrusted = Environment.GetEnvironmentVariable("NEBULA_LINK_ALLOW_UNTRUSTED") is "1" or "true";

    static NebulaLinkApis()
    {
        var overrideUrl = Environment.GetEnvironmentVariable("NEBULA_LINK_TENANT_URL");
        if (!string.IsNullOrWhiteSpace(overrideUrl) &&
            Uri.TryCreate(overrideUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(parsed.UserInfo))
        {
            DebugOverrideOrigin = GetOrigin(parsed);
            DebugOverrideBase = BaseOf(parsed);
        }
    }

    /// <summary>The origin of <c>NEBULA_LINK_TENANT_URL</c>.</summary>
    public static string? DebugOverrideOrigin { get; }

    /// <summary>The API base of <c>NEBULA_LINK_TENANT_URL</c>.</summary>
    public static string? DebugOverrideBase { get; }

    /// <summary>Forgets the fetched list.</summary>
    public static void ResetForTests()
    {
        lock (Lock)
        {
            _fetched = [];
            Status = ApisStatus.Loading;
        }
    }

    static partial void DebugAllowUntrusted(ref bool allowed)
    {
        allowed = Untrusted;
    }

    static partial void DebugCanPair(ref bool canPair)
    {
        canPair = true;
    }

    static partial void DebugDefaultBase(ref string? apiBase)
    {
        apiBase = DebugOverrideBase ?? apiBase;
    }

    static partial void DebugAllowBase(Uri uri, string normalized, ref bool allowed)
    {
        allowed = Untrusted || IsLoopback(uri) || (DebugOverrideBase != null && normalized == DebugOverrideBase);
    }

    static partial void DebugAllowHttp(Uri uri, ref bool allowed)
    {
        allowed = Untrusted || IsLoopback(uri) || DebugOverrideOrigin == GetOrigin(uri);
    }

    static partial void DebugAllowListEntry(Uri uri, ref bool allowed)
    {
        allowed = IsLoopback(uri);
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.Scheme is "http" or "https" && uri.IsLoopback && uri.Host is "localhost" or "127.0.0.1" or "[::1]";
    }
}
