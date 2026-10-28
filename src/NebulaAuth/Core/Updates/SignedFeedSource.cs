using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.Model.Update;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace NebulaAuth.Core.Updates;

/// <summary>
///     Release metadata signed together with the asset list (the "Nebula" object of <c>releases.{channel}.json</c>).
/// </summary>
public sealed record FeedMetadata(
    string Channel,
    string Version,
    DateTimeOffset Published,
    string? ReleaseUrl,
    string? ManualUrl);

public sealed class UpdateException(UpdateFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpdateFailureKind Kind { get; } = kind;
}

/// <summary>
///     Velopack update source that only accepts a feed signed with the pinned key (<see cref="UpdateTrust" />).
///     <list type="bullet">
///         <item><c>releases.{channel}.json</c> is verified against <c>releases.{channel}.json.sig</c> (base64 ECDSA P-256 /
///             SHA-256 signature, DER as written by <c>openssl dgst -sha256 -sign</c>) before it is parsed.</item>
///         <item>The signed "Nebula" object must name the requested channel (a feed of another OS or of beta is rejected)
///             and must not be older than the newest feed seen before (replay of an old signed feed).</item>
///         <item>Every asset needs a SHA-256 and an absolute https URL: Velopack checks each downloaded package against
///             that SHA-256, so the signature covers the package bytes.</item>
///     </list>
/// </summary>
public sealed class SignedFeedSource : IUpdateSource
{
    public const string PACKAGE_ID = "NebulaAuth";
    private const double DOWNLOAD_TIMEOUT_MINUTES = 30;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _baseUrl;
    private readonly bool _allowHttp;
    private readonly string _publicKeyPem;
    private readonly IFileDownloader _downloader = new HttpClientFileDownloader();

    public SignedFeedSource(string baseUrl, string publicKeyPem, bool allowHttp = false)
    {
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        _publicKeyPem = publicKeyPem;
        _allowHttp = allowHttp;
    }

    /// <summary>
    ///     Metadata of the last feed that passed verification.
    /// </summary>
    public FeedMetadata? LastMetadata { get; private set; }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var name = $"releases.{channel}.json";
        byte[] json;
        string signature;
        try
        {
            json = await Http.GetByteArrayAsync(_baseUrl + name).ConfigureAwait(false);
            signature = await Http.GetStringAsync(_baseUrl + name + ".sig").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException(UpdateFailureKind.Network, $"Can't download the update feed {name}: {ex.Message}",
                ex);
        }

        if (!VerifySignature(json, signature, _publicKeyPem))
        {
            throw new UpdateException(UpdateFailureKind.Signature, $"The signature of {name} is invalid");
        }

        var text = Encoding.UTF8.GetString(json).TrimStart('﻿');
        var metadata = ParseMetadata(text, name);
        if (!string.Equals(metadata.Channel, channel, StringComparison.Ordinal))
        {
            throw new UpdateException(UpdateFailureKind.Signature,
                $"{name} is signed for the channel '{metadata.Channel}', not '{channel}'");
        }

        var settings = UpdateSettings.Load();
        if (settings.LastFeedPublished is { } seen && metadata.Published < seen)
        {
            throw new UpdateException(UpdateFailureKind.Signature,
                $"{name} was published {metadata.Published:O}, older than the feed seen before ({seen:O})");
        }

        var feed = VelopackAssetFeed.FromJson(text);
        if (!SemanticVersion.TryParse(metadata.Version, out var feedVersion))
        {
            throw new UpdateException(UpdateFailureKind.Signature, $"{name} has an invalid version '{metadata.Version}'");
        }

        foreach (var asset in feed.Assets)
        {
            ValidateAsset(asset, feedVersion, name);
        }

        if (settings.LastFeedPublished == null || metadata.Published > settings.LastFeedPublished)
        {
            settings.LastFeedPublished = metadata.Published;
            settings.Save();
        }

        LastMetadata = metadata;
        logger.Info($"Signed feed {name} verified: {metadata.Version}, published {metadata.Published:O}, " +
                    $"{feed.Assets.Length} asset(s)");
        return feed;
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        // Validated in GetReleaseFeed already; checked again because this is the URL that is actually requested
        if (!IsAllowedUrl(releaseEntry.FileName))
        {
            throw new UpdateException(UpdateFailureKind.Signature, $"Refusing to download {releaseEntry.FileName}");
        }

        logger.Info($"Downloading {releaseEntry.FileName}");
        return _downloader.DownloadFile(releaseEntry.FileName, localFile, progress, null, DOWNLOAD_TIMEOUT_MINUTES,
            cancelToken);
    }

    /// <summary>
    ///     True when <paramref name="signatureBase64" /> is a valid signature of <paramref name="data" /> by
    ///     <paramref name="publicKeyPem" />. Accepts DER (openssl) and raw r||s (IEEE P1363) encodings.
    /// </summary>
    public static bool VerifySignature(byte[] data, string signatureBase64, string publicKeyPem)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using var key = ImportKey(publicKeyPem);
        if (key == null) return false;
        var format = signature.Length == 64
            ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            : DSASignatureFormat.Rfc3279DerSequence;
        try
        {
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, format);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The pinned key as an ECDSA P-256 key, or null when it is missing / not a P-256 public key (placeholder).
    /// </summary>
    public static ECDsa? ImportKey(string publicKeyPem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256)
            {
                key.Dispose();
                return null;
            }

            return key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            return null;
        }
    }

    public static bool IsKeyConfigured(string publicKeyPem)
    {
        using var key = ImportKey(publicKeyPem);
        return key != null;
    }

    private static FeedMetadata ParseMetadata(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var nebula = doc.RootElement.GetProperty("Nebula");
            var published = DateTimeOffset.Parse(nebula.GetProperty("Published").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            return new FeedMetadata(
                nebula.GetProperty("Channel").GetString()!,
                nebula.GetProperty("Version").GetString()!,
                published,
                nebula.TryGetProperty("ReleaseUrl", out var release) ? release.GetString() : null,
                nebula.TryGetProperty("ManualUrl", out var manual) ? manual.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException
                                       or InvalidOperationException or NullReferenceException)
        {
            throw new UpdateException(UpdateFailureKind.Signature, $"{name} has no valid release metadata", ex);
        }
    }

    private void ValidateAsset(VelopackAsset asset, SemanticVersion feedVersion, string name)
    {
        if (!string.Equals(asset.PackageId, PACKAGE_ID, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateFailureKind.Signature, $"{name}: unexpected package {asset.PackageId}");
        if (asset.SHA256 is not {Length: 64} sha || !sha.All(Uri.IsHexDigit))
            throw new UpdateException(UpdateFailureKind.Signature, $"{name}: {asset.FileName} has no SHA-256");
        if (!IsAllowedUrl(asset.FileName))
            throw new UpdateException(UpdateFailureKind.Signature, $"{name}: {asset.FileName} is not an absolute https URL");
        if (asset.Version > feedVersion)
            throw new UpdateException(UpdateFailureKind.Signature,
                $"{name}: {asset.FileName} is newer than the signed release version {feedVersion}");
    }

    private bool IsAllowedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps || (_allowHttp && uri.Scheme == Uri.UriSchemeHttp);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient {Timeout = TimeSpan.FromSeconds(30)};
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue {NoCache = true};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NebulaAuth-Updater");
        return client;
    }
}
