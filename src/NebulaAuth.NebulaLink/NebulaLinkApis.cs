using System;
using System.Collections.Generic;
using System.Linq;

namespace NebulaAuth.NebulaLink;

public enum ApisStatus
{
    /// <summary>The list of allowed Achiefy API bases is being fetched.</summary>
    Loading,

    Available,

    /// <summary>The list could not be fetched: new pairings are not possible (an existing link keeps working).</summary>
    Unavailable
}

/// <summary>
///     The Achiefy API bases NebulaAuth may pair with. The list is published in the NebulaAuth repository (links.json key
///     <c>NebulaLinkApis</c>, fetched by the app); it is the only source of these addresses, so a new address needs no
///     app update. A base is a full address (scheme, host, port and path, e.g. https://example.com/api); a pairing link is
///     accepted only when its base equals a published one (host case-insensitive) and is https. Debug builds can
///     accept more addresses for local stands (the <c>Debug*</c> partial methods; they do not exist in Release
///     builds). An existing link connects to the base stored (and checked) at pairing time, whatever the list says
///     later. Nothing here opens a connection.
/// </summary>
public static partial class NebulaLinkApis
{
    /// <summary>
    ///     Suffix of a pairing link after the API base (<c>&lt;base&gt;/nebula/pair#&lt;CODE&gt;</c>); the code is in the
    ///     fragment (never sent to a server).
    /// </summary>
    public const string PAIR_SUFFIX = "/nebula/pair";

    private static readonly object Lock = new();
    private static IReadOnlyList<string> _fetched = [];

    /// <summary>An <c>apiOrigin</c> mismatch is only logged. Always false in Release builds.</summary>
    public static bool AllowUntrusted
    {
        get
        {
            var allowed = false;
            DebugAllowUntrusted(ref allowed);
            return allowed;
        }
    }

    public static ApisStatus Status { get; private set; } = ApisStatus.Loading;

    /// <summary>Raised when the list or its status changed (any thread).</summary>
    public static event Action? Changed;

    /// <summary>The fetched API bases (normalized, see <see cref="NormalizeBase" />), in the published order.</summary>
    public static IReadOnlyList<string> Allowed
    {
        get
        {
            lock (Lock) return _fetched;
        }
    }

    /// <summary>A new pairing can be started (the list is there, or a Debug build with its local addresses).</summary>
    public static bool CanPair
    {
        get
        {
            var canPair = Status == ApisStatus.Available && Allowed.Count > 0;
            DebugCanPair(ref canPair);
            return canPair;
        }
    }

    /// <summary>The API base a bare pairing code is redeemed at: the first published base.</summary>
    public static string? DefaultBase
    {
        get
        {
            var apiBase = Allowed.FirstOrDefault();
            DebugDefaultBase(ref apiBase);
            return apiBase;
        }
    }

    /// <summary>A fetch started; a list fetched earlier in this session stays usable meanwhile.</summary>
    public static void SetLoading()
    {
        lock (Lock)
        {
            if (Status == ApisStatus.Available) return;
            Status = ApisStatus.Loading;
        }

        Changed?.Invoke();
    }

    /// <summary>The published list (null: it could not be fetched). Entries that are not valid bases are ignored.</summary>
    public static void SetFetched(IEnumerable<string?>? bases)
    {
        lock (Lock)
        {
            if (bases == null)
            {
                // A list fetched earlier in this session stays valid
                Status = _fetched.Count == 0 ? ApisStatus.Unavailable : ApisStatus.Available;
            }
            else
            {
                _fetched = bases.Select(NormalizeEntry).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                Status = _fetched.Count > 0 ? ApisStatus.Available : ApisStatus.Unavailable;
            }
        }

        NebulaLinkLog.Info($"Allowed API bases: {Status} ({Allowed.Count})");
        Changed?.Invoke();
    }

    /// <summary>
    ///     A pairing may be started at this API base: well-formed and published exactly.
    /// </summary>
    public static bool IsAllowedBase(string? apiBase)
    {
        var normalized = NormalizeBase(apiBase);
        if (normalized == null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || !IsWellFormed(uri)) return false;
        var allowed = Allowed.Contains(normalized, StringComparer.Ordinal);
        if (!allowed) DebugAllowBase(uri, normalized, ref allowed);
        return allowed;
    }

    /// <summary>
    ///     Scheme and host lower case, the port when not default, the path without a trailing '/' (case kept); null when
    ///     it is not an absolute http(s) address without user info, query and fragment.
    /// </summary>
    public static string? NormalizeBase(string? apiBase)
    {
        if (string.IsNullOrWhiteSpace(apiBase)) return null;
        if (!Uri.TryCreate(apiBase.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return null;
        return BaseOf(uri);
    }

    /// <summary>
    ///     Absolute https address without user info. Checked again before every request: a stored link address that is
    ///     not well-formed is never used.
    /// </summary>
    public static bool IsWellFormed(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return !string.IsNullOrEmpty(uri.Host);
        var allowed = false;
        if (uri.Scheme == Uri.UriSchemeHttp) DebugAllowHttp(uri, ref allowed);
        return allowed;
    }

    /// <summary>Scheme + host + port, lower case, without a path ("https://example.com").</summary>
    public static string GetOrigin(Uri uri)
    {
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>The apiOrigin returned by the server equals <paramref name="expectedOrigin" /> (scheme + host + port).</summary>
    public static bool SameOrigin(string? apiOrigin, string expectedOrigin)
    {
        if (string.IsNullOrWhiteSpace(apiOrigin)) return false;
        if (!Uri.TryCreate(apiOrigin.Trim(), UriKind.Absolute, out var uri)) return false;
        return GetOrigin(uri) == expectedOrigin;
    }

    /// <summary>Host (and port when not default) for display: "example.com".</summary>
    public static string DisplayHost(string originOrUrl)
    {
        if (!Uri.TryCreate(originOrUrl, UriKind.Absolute, out var uri)) return originOrUrl;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    private static string BaseOf(Uri uri)
    {
        return GetOrigin(uri) + uri.AbsolutePath.TrimEnd('/');
    }

    private static string? NormalizeEntry(string? entry)
    {
        var normalized = NormalizeBase(entry);
        if (normalized == null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return null;
        var allowed = uri.Scheme == Uri.UriSchemeHttps;
        if (!allowed) DebugAllowListEntry(uri, ref allowed);
        return allowed ? normalized : null;
    }

    static partial void DebugAllowUntrusted(ref bool allowed);
    static partial void DebugCanPair(ref bool canPair);
    static partial void DebugDefaultBase(ref string? apiBase);
    static partial void DebugAllowBase(Uri uri, string normalized, ref bool allowed);
    static partial void DebugAllowHttp(Uri uri, ref bool allowed);
    static partial void DebugAllowListEntry(Uri uri, ref bool allowed);
}
