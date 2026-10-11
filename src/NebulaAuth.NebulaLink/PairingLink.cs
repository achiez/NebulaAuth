using System;
using System.Collections.Generic;

namespace NebulaAuth.NebulaLink;

/// <summary>Where and with which code a pairing is redeemed (parsed from what the user pasted).</summary>
/// <param name="Origin">Scheme + host + port of the API base ("https://example.com").</param>
/// <param name="TenantBaseUrl">The API base: the link without its "/nebula/pair#..." suffix, a published base (<see cref="NebulaLinkApis" />).</param>
/// <param name="Code">Normalized code "XXXXX-XXXXX".</param>
public sealed record PairingTarget(string Origin, string TenantBaseUrl, string Code);

/// <summary>
///     The pairing input: the link the Achiefy cabinet shows (<c>&lt;API base&gt;/nebula/pair#&lt;CODE&gt;</c>, e.g.
///     https://example.com/api/nebula/pair#ABCDE-FGHIJ; the code is in the fragment) or the bare code (redeemed at
///     <see cref="NebulaLinkApis.DefaultBase" />). The API base is the link without the suffix and must equal a published
///     base. Parsing is local and happens before any request.
/// </summary>
public static class PairingLink
{
    public const string ERROR_EMPTY = "input_empty";
    public const string ERROR_LINK_INVALID = "link_invalid";
    public const string ERROR_ORIGIN_NOT_ALLOWED = "origin_not_allowed";
    public const string ERROR_NO_DEFAULT_ORIGIN = "no_default_origin";

    /// <summary>Parses the input. On failure <paramref name="error" /> is one of the ERROR_* codes or <see cref="NebulaApiException.CODE_PAIRING_CODE_INVALID" />.</summary>
    public static bool TryParse(string? input, out PairingTarget? target, out string? error)
    {
        target = null;
        error = null;
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = ERROR_EMPTY;
            return false;
        }

        if (!LooksLikeLink(text))
        {
            var code = NormalizeCode(text);
            if (code == null)
            {
                error = NebulaApiException.CODE_PAIRING_CODE_INVALID;
                return false;
            }

            var apiBase = NebulaLinkApis.DefaultBase;
            if (apiBase == null || !Uri.TryCreate(apiBase, UriKind.Absolute, out var baseUri))
            {
                error = ERROR_NO_DEFAULT_ORIGIN;
                return false;
            }

            target = new PairingTarget(NebulaLinkApis.GetOrigin(baseUri), apiBase, code);
            return true;
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        var path = "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !(path = uri.AbsolutePath.TrimEnd('/')).EndsWith(NebulaLinkApis.PAIR_SUFFIX, StringComparison.OrdinalIgnoreCase))
        {
            error = ERROR_LINK_INVALID;
            return false;
        }

        var linkOrigin = NebulaLinkApis.GetOrigin(uri);
        var linkBase = linkOrigin + path[..^NebulaLinkApis.PAIR_SUFFIX.Length];
        if (!NebulaLinkApis.IsAllowedBase(linkBase))
        {
            error = ERROR_ORIGIN_NOT_ALLOWED;
            return false;
        }

        var linkCode = NormalizeCode(Uri.UnescapeDataString(uri.Fragment.TrimStart('#')));
        if (linkCode == null)
        {
            error = NebulaApiException.CODE_PAIRING_CODE_INVALID;
            return false;
        }

        target = new PairingTarget(linkOrigin, NebulaLinkApis.NormalizeBase(linkBase)!, linkCode);
        return true;
    }

    /// <summary>The input is a link (an address with the pairing path), not a bare code.</summary>
    public static bool LooksLikeLink(string text)
    {
        return text.Contains("://", StringComparison.Ordinal) || text.Contains('/', StringComparison.Ordinal) ||
               text.Contains('#', StringComparison.Ordinal);
    }

    /// <summary>
    ///     Normalizes a pairing code: upper case, Crockford substitutions (O→0, I/L→1), separators removed. Null when it
    ///     is not 10 base32 characters.
    /// </summary>
    public static string? NormalizeCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var chars = new List<char>(10);
        foreach (var raw in input.Trim().ToUpperInvariant())
        {
            var c = raw switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => raw
            };
            if (c is '-' or ' ' or '_') continue;
            if (!(char.IsAsciiDigit(c) || c is >= 'A' and <= 'Z') || c == 'U') return null;
            chars.Add(c);
        }

        if (chars.Count != 10) return null;
        var s = new string(chars.ToArray());
        return s[..5] + "-" + s[5..];
    }
}
