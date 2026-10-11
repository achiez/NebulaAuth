using System;
using System.Text.RegularExpressions;
using NLog;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Every log line of Nebula Link goes through here. Callers pass only request ids, account ids, command types, modules
///     and result codes (never codes, credentials, claim secrets or payloads); the text is still passed through
///     <see cref="Sanitizer" /> as a second line of defence.
/// </summary>
public static partial class NebulaLinkLog
{
    private const string MASK = "***";
    private static readonly Logger Log = LogManager.GetLogger("NebulaLink");

    /// <summary>
    ///     Masks secrets in log text. The app sets its full sanitizer (URL credentials, tokens, ...); the default masks the
    ///     <see cref="RuntimeSecrets" />, device credentials, pairing codes and secret key/value pairs.
    /// </summary>
    public static Func<string, string> Sanitizer { get; set; } = DefaultSanitize;

    public static void Debug(string message)
    {
        Write(LogLevel.Debug, message, null);
    }

    public static void Info(string message)
    {
        Write(LogLevel.Info, message, null);
    }

    public static void Warn(string message, Exception? ex = null)
    {
        Write(LogLevel.Warn, message, ex);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write(LogLevel.Error, message, ex);
    }

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        try
        {
            var text = Sanitizer(message);
            if (ex != null) text += " | " + Sanitizer(ex.GetType().Name + ": " + ex.Message);
            Log.Log(level, "[NebulaLink] {text}", text);
        }
        catch
        {
            // Logging must never break the integration
        }
    }

    /// <summary>Short sanitized text of an exception for the "diagnostic" field (≤ 200 chars, EN, no secrets).</summary>
    public static string Diagnostic(Exception ex)
    {
        var text = Sanitizer(ex.GetType().Name + ": " + ex.Message);
        // URLs may carry tokens in the query: keep only the path part
        text = QueryPart().Replace(text, "?…");
        return text.Length <= 200 ? text : text[..200];
    }

    private static string DefaultSanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in RuntimeSecrets.Snapshot())
        {
            if (secret.Length >= 3) text = text.Replace(secret, MASK, StringComparison.Ordinal);
        }

        text = DeviceCredential().Replace(text, "nbl1." + MASK);
        text = PairingCode().Replace(text, "*****-*****");
        return SecretPair().Replace(text, "${key}${sep}" + MASK);
    }

    [GeneratedRegex(@"\?[^\s""']*")]
    private static partial Regex QueryPart();

    [GeneratedRegex(@"nbl1\.[0-9a-fA-F]{32}\.[A-Za-z0-9_-]{43}")]
    private static partial Regex DeviceCredential();

    [GeneratedRegex(@"\b[0-9A-HJKMNP-TV-Z]{5}-?[0-9A-HJKMNP-TV-Z]{5}\b")]
    private static partial Regex PairingCode();

    [GeneratedRegex(
        @"(?<key>token|password|secret|claimSecret|claim_secret|credential|guardCode|guard_code|pairingCode|pairing_code|(?<![A-Za-z0-9_])code)(?<sep>[""']?\s*[=:]\s*[""']?)[^""'&\s,;})]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretPair();
}
