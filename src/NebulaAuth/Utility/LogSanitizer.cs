using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NebulaAuth.Utility;

/// <summary>
///     Removes secrets from text that goes to the log (exception messages and stack traces): credentials in URLs, Steam
///     tokens (JWT), "token=..." / "password: ..." pairs and the given known values (proxy password).
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
                // Very short values would mask unrelated text; such values are not secrets worth hiding either
                if (string.IsNullOrEmpty(secret) || secret.Length < 3) continue;
                text = text.Replace(secret, MASK, StringComparison.Ordinal);
            }
        }

        text = UrlCredentials().Replace(text, "${scheme}" + MASK + ":" + MASK + "@");
        text = Jwt().Replace(text, "<token>");
        text = SecretPair().Replace(text, "${key}${sep}" + MASK);
        return text;
    }

    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)[^/\s:@]+:[^/\s@]*@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(
        @"(?<key>(?:access|refresh|oauth|auth|session)?_?token|steamLoginSecure|sessionid|password|passwd|secret|shared_secret|identity_secret|revocation_code)(?<sep>[""']?\s*[=:]\s*[""']?)[^""'&\s,;})]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretPair();
}
