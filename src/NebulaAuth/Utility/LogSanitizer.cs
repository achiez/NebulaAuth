using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NebulaAuth.NebulaLink;

namespace NebulaAuth.Utility;

/// <summary>
///     Removes secrets from text that goes to the log (exception messages and stack traces): credentials in URLs, Steam
///     tokens (JWT), "token=..." / "password: ..." pairs, Nebula Link device credentials, pairing codes, Steam Guard / pairing
///     values in key/value pairs, the values registered in <see cref="RuntimeSecrets" /> and the given known values
///     (proxy password).
/// </summary>
public static partial class LogSanitizer
{
    private const string MASK = "***";

    public static string Sanitize(string text, IEnumerable<string?>? secrets = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (secrets != null)
        {
            foreach (var secret in secrets)
            {
                text = MaskValue(text, secret);
            }
        }

        foreach (var secret in RuntimeSecrets.Snapshot())
        {
            text = MaskValue(text, secret);
        }

        text = UrlCredentials().Replace(text, "${scheme}" + MASK + ":" + MASK + "@");
        text = Jwt().Replace(text, "<token>");
        text = DeviceCredential().Replace(text, "nbl1." + MASK);
        text = PairingCode().Replace(text, "*****-*****");
        text = SecretPair().Replace(text, "${key}${sep}" + MASK);
        return text;
    }

    private static string MaskValue(string text, string? secret)
    {
        // Very short values would mask unrelated text; such values are not secrets worth hiding either
        if (string.IsNullOrEmpty(secret) || secret.Length < 3) return text;
        return text.Replace(secret, MASK, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)[^/\s:@]+:[^/\s@]*@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    // Nebula Link device credential: nbl1.<linkId 32 hex>.<secret 43 base64url>
    [GeneratedRegex(@"nbl1\.[0-9a-fA-F]{32}\.[A-Za-z0-9_-]{43}")]
    private static partial Regex DeviceCredential();

    // Nebula Link pairing code: 10 Crockford base32 characters shown as XXXXX-XXXXX
    [GeneratedRegex(@"\b[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}\b")]
    private static partial Regex PairingCode();

    [GeneratedRegex(
        @"(?<key>(?:access|refresh|oauth|auth|session)?_?token|steamLoginSecure|sessionid|password|passwd|secret|shared_secret|identity_secret|revocation_code|pairing_code|pairingCode|claimSecret|claim_secret|device_credential|credential|guardCode|guard_code|(?<![A-Za-z0-9_])code)(?<sep>[""']?\s*[=:]\s*[""']?)[^""'&\s,;})]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretPair();
}
