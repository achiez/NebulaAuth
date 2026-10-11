using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NebulaAuth.NebulaLink;

/// <summary>Creates the HTTP handlers of every Nebula Link request (pairing and hub).</summary>
public interface INebulaHttpFactory
{
    /// <summary>
    ///     A handler with redirects disabled (any 3xx is an error), default TLS validation (no custom certificate
    ///     callback) and the system proxy.
    /// </summary>
    HttpMessageHandler CreateHandler();
}

public sealed class NebulaHttpFactory : INebulaHttpFactory
{
    public HttpMessageHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
    }
}

/// <summary>An Achiefy HTTP call failed. <see cref="Code" /> is error.info.code of the envelope or a local code.</summary>
public sealed class NebulaApiException(string code, string message, int? httpStatus = null, Exception? inner = null)
    : Exception(message, inner)
{
    public const string CODE_NETWORK = "network";
    public const string CODE_REDIRECT = "redirect";
    public const string CODE_BAD_RESPONSE = "bad_response";
    public const string CODE_PAIRING_CODE_INVALID = "nebula.pairing_code_invalid";
    public const string CODE_PAIRING_NOT_FOUND = "nebula.pairing_not_found";
    public const string CODE_PROTOCOL_INCOMPATIBLE = "nebula.protocol_incompatible";

    /// <summary>The server reported another API origin than the one the pairing was redeemed at.</summary>
    public const string CODE_ORIGIN_MISMATCH = "origin_mismatch";

    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>
///     Device HTTP endpoints of TenantAPI (§2.4): redeem, claim, cancel. Parses the ApiResult envelope; an empty 429 is
///     <c>rate_limited</c>. The request bodies (pairing code, claim secret) are never logged.
/// </summary>
public sealed class PairingClient : IDisposable
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.DateTimeOffset
    };

    private readonly HttpClient _client;
    private readonly string _tenantBase;

    public PairingClient(HttpMessageHandler handler, string tenantBaseUrl)
    {
        _client = new HttpClient(handler, true) {Timeout = TimeSpan.FromSeconds(20)};
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("NebulaAuth/" + LinkDeviceInfo.AppVersion);
        _tenantBase = tenantBaseUrl.TrimEnd('/');
    }

    public Task<PairingRedeemResponse> RedeemAsync(PairingRedeemRequest request, CancellationToken ct)
    {
        return PostAsync<PairingRedeemResponse>("nebula/device/pairing/redeem", request, ct);
    }

    public Task<PairingClaimResponse> ClaimAsync(PairingClaimRequest request, CancellationToken ct)
    {
        return PostAsync<PairingClaimResponse>("nebula/device/pairing/claim", request, ct);
    }

    public Task CancelAsync(PairingCancelRequest request, CancellationToken ct)
    {
        return PostAsync<JToken>("nebula/device/pairing/cancel", request, ct, true);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct, bool allowEmpty = false)
    {
        var uri = new Uri(_tenantBase + "/" + path);
        if (!NebulaLinkApis.IsWellFormed(uri))
            throw new NebulaApiException(PairingLink.ERROR_ORIGIN_NOT_ALLOWED, "The Achiefy address is not allowed");
        using var content = new StringContent(JsonConvert.SerializeObject(body, JsonSettings), Encoding.UTF8,
            "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _client.PostAsync(uri, content, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new NebulaApiException(NebulaApiException.CODE_NETWORK, "Achiefy is not reachable", null, ex);
        }

        using (response)
        {
            var status = (int) response.StatusCode;
            if (status is >= 300 and < 400)
                throw new NebulaApiException(NebulaApiException.CODE_REDIRECT,
                    "Achiefy answered with a redirect; redirects are not followed", status);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new NebulaApiException(NebulaResultCodes.RateLimited, "Too many requests", status);

            JObject? envelope = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(text)) envelope = JObject.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new NebulaApiException(NebulaApiException.CODE_BAD_RESPONSE,
                    $"Unexpected Achiefy response (HTTP {status})", status, ex);
            }

            if (envelope?.Value<bool?>("success") == true)
            {
                var data = envelope["data"];
                if (data == null || data.Type == JTokenType.Null)
                {
                    if (allowEmpty) return default!;
                    throw new NebulaApiException(NebulaApiException.CODE_BAD_RESPONSE, "Empty Achiefy response",
                        status);
                }

                return data.ToObject<T>(JsonSerializer.Create(JsonSettings))!;
            }

            var error = envelope?["error"];
            var code = error?["info"]?.Value<string>("code") ?? $"http_{status}";
            var message = error?.Value<string>("message") ?? $"HTTP {status}";
            throw new NebulaApiException(code, message, status);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
