using System;
using NebulaAuth.Model.Browser;

namespace NebulaAuth.Core.Browser;

public enum BrowserError
{
    /// <summary>The user closed the window or cancelled the sign-in: no error message.</summary>
    Cancelled,

    ProxyAuthFailed,
    ProxyUnreachable,
    ProxyRefused,
    ProxyTimeout,
    ProxyProtocol,

    /// <summary>The platform can't route the engine through the proxy (e.g. macOS before 14).</summary>
    ProxyNotSupported,

    /// <summary>No valid Steam session: refresh / sign-in failed or was cancelled.</summary>
    SessionUnavailable,

    /// <summary>Steam Community did not show the expected account (signed out, other account, sign-in page).</summary>
    SessionRejected,

    /// <summary>Steam Community could not be loaded.</summary>
    SteamUnreachable,

    /// <summary>The browser engine is missing or failed to start.</summary>
    EngineUnavailable,

    /// <summary>The temporary profile path is too long for the engine.</summary>
    ProfilePathTooLong
}

/// <summary>
///     Failure of opening the embedded browser. The message is diagnostic only (log); the user sees the localized text
///     of <see cref="Error" />. Never contains cookies, tokens or proxy credentials.
/// </summary>
public sealed class BrowserOpenException(BrowserError error, string? detail = null, Exception? inner = null)
    : Exception(detail ?? error.ToString(), inner)
{
    public BrowserError Error { get; } = error;

    /// <summary>Optional text shown after the localized error (e.g. the reason of a failed sign-in).</summary>
    public string? UserDetail { get; init; }

    public static BrowserError FromUpstream(UpstreamFailure failure)
    {
        return failure switch
        {
            UpstreamFailure.AuthFailed => BrowserError.ProxyAuthFailed,
            UpstreamFailure.Unreachable => BrowserError.ProxyUnreachable,
            UpstreamFailure.Refused => BrowserError.ProxyRefused,
            UpstreamFailure.Timeout => BrowserError.ProxyTimeout,
            _ => BrowserError.ProxyProtocol
        };
    }
}
