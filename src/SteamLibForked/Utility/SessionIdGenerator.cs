using System.Security.Cryptography;

namespace SteamLib.Utility;

/// <summary>
///     Generates a <c>sessionid</c> on the client. Steam uses it only as a CSRF token that has to match the cookie,
///     so a value in Steam's own format (12 random bytes, lower-case hex) is accepted the same way an issued one is.
/// </summary>
public static class SessionIdGenerator
{
    private const int SESSION_ID_BYTES = 12;

    public static string Generate()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(SESSION_ID_BYTES)).ToLowerInvariant();
    }
}
