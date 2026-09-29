using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NebulaAuth.Model.QrLogin;

/// <summary>
///     Data of a Steam login QR code: the challenge URL <c>https://s.team/q/{version}/{client_id}</c> (a query string is
///     allowed). The URL itself is never opened or sent anywhere; only the two numbers are used. The client id identifies
///     one login attempt, so neither the URL nor the client id are logged or shown.
/// </summary>
public readonly partial record struct SteamQrChallenge(int Version, ulong ClientId)
{
    /// <summary>The version is signed as UInt16.</summary>
    public const int MAX_VERSION = ushort.MaxValue;

    [GeneratedRegex(@"^https?://s\.team/q/(?<version>\d{1,10})/(?<client>\d{1,20})(?:\?[^#\s]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChallengeUrlRegex();

    /// <summary>
    ///     Parses a QR code text. Returns false for anything but a Steam login challenge URL with a version in
    ///     1..65535 and a non-zero client id.
    /// </summary>
    public static bool TryParse(string? text, out SteamQrChallenge challenge)
    {
        challenge = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = ChallengeUrlRegex().Match(text.Trim());
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups["version"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture,
                out var version) || version is < 1 or > MAX_VERSION)
            return false;

        if (!ulong.TryParse(match.Groups["client"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture,
                out var clientId) || clientId == 0)
            return false;

        challenge = new SteamQrChallenge(version, clientId);
        return true;
    }

    /// <summary>Does not include the client id (it identifies the login attempt).</summary>
    public override string ToString()
    {
        return FormattableString.Invariant($"SteamQrChallenge(v{Version})");
    }
}
