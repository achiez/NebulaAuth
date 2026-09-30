using System;

namespace NebulaAuth.Core.Browser;

public enum NavigationDecision
{
    Allow,

    /// <summary>Not http(s): external app protocols (steam://), files, scripts.</summary>
    BlockScheme,

    /// <summary>
    ///     Steam sign-out: the page session is derived from the account's mobile session, so signing out on the site can end
    ///     the session NebulaAuth uses for confirmations. The window is closed instead.
    /// </summary>
    BlockSignOut
}

/// <summary>Which top-level navigations the embedded browser allows (same rules on every platform).</summary>
public static class NavigationPolicy
{
    public static NavigationDecision Check(Uri? uri)
    {
        if (uri == null) return NavigationDecision.BlockScheme;
        if (uri.Scheme == "about" && uri.AbsoluteUri == "about:blank") return NavigationDecision.Allow;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return NavigationDecision.BlockScheme;
        return IsSteamSignOut(uri) ? NavigationDecision.BlockSignOut : NavigationDecision.Allow;
    }

    public static bool IsSteamSignOut(Uri uri)
    {
        if (!IsSteamHost(uri.Host)) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/logout", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSteamHost(string host)
    {
        return host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("steampowered.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".steampowered.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("steam.tv", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".steam.tv", StringComparison.OrdinalIgnoreCase);
    }
}
