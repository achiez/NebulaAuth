using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using NebulaAuth.NebulaLink.Protocol;
using SteamLib;
using SteamLib.SteamMobile.Confirmations;

namespace NebulaAuth.NebulaLink.Confirmations;

/// <summary>
///     Interactive: the manual screen (dialogs and snackbars allowed, original exceptions). Headless: Achiefy requests
///     (no UI, typed result codes through <see cref="NebulaCodeException" />).
/// </summary>
public enum SessionMode
{
    Interactive,
    Headless
}

/// <summary>An operation failed with a protocol result code (<see cref="NebulaResultCodes" />).</summary>
public sealed class NebulaCodeException(string code, string? diagnostic = null, Exception? inner = null)
    : Exception(diagnostic ?? code, inner)
{
    public string Code { get; } = code;
}

/// <summary>Which snapshot may answer a read.</summary>
/// <param name="MaxAge">A snapshot fetched at most this long ago is reused.</param>
/// <param name="MinFetchStartedAt">
///     When set, only a snapshot whose fetch started at or after this time is accepted (a negative answer must not come
///     from a list fetched before the operation was created).
/// </param>
public sealed record SnapshotPolicy(TimeSpan MaxAge, DateTimeOffset? MinFetchStartedAt = null)
{
    /// <summary>Manual screen: reuse a list fetched within the minimum fetch interval.</summary>
    public static SnapshotPolicy Manual => new(ConfirmationService.MinFetchInterval);

    public bool Accepts(ConfirmationSnapshot snapshot, DateTimeOffset now)
    {
        return now - snapshot.FetchedAt <= MaxAge &&
               (MinFetchStartedAt == null || snapshot.FetchStartedAt >= MinFetchStartedAt);
    }
}

public sealed record ConfirmationSnapshot(
    DateTimeOffset FetchStartedAt,
    DateTimeOffset FetchedAt,
    IReadOnlyList<Confirmation> Items);

public sealed record SnapshotResult(IReadOnlyList<Confirmation> Items, DateTimeOffset FetchedAt);

/// <summary>check_confirmation result: pending / not_found (+ resolution) or an error code.</summary>
public sealed record ConfirmationCheckOutcome(string Code, string? Resolution = null, string? Diagnostic = null);

/// <summary>confirm result: top-level code; <see cref="Items" /> only when the code is ok.</summary>
public sealed record ConfirmOutcome(string Code, IReadOnlyList<NebulaOperationResult>? Items, string? Diagnostic = null);

public enum TransportSendStatus
{
    /// <summary>Steam answered success.</summary>
    Success,

    /// <summary>Steam answered success:false.</summary>
    Rejected,

    /// <summary>Failed before the request was written (connection, proxy, DNS, TLS): nothing happened in Steam.</summary>
    NotSent,

    /// <summary>Failed after the request may have been written: verify the Steam state first.</summary>
    Unknown
}

public sealed record TransportSendResult(TransportSendStatus Status, string? Code = null, string? Diagnostic = null);

/// <summary>Protocol operation types ⇄ Steam confirmation types (the same numbers in every SteamLib).</summary>
public static class ConfirmationTypes
{
    public static int? FromOperationType(string? operationType)
    {
        return operationType switch
        {
            NebulaOperationTypes.Trade => (int) ConfirmationType.Trade,
            NebulaOperationTypes.MarketListing => (int) ConfirmationType.MarketSellTransaction,
            NebulaOperationTypes.MarketPurchase => (int) ConfirmationType.Purchase,
            _ => null
        };
    }

    public static string? ToOperationType(int type)
    {
        return (ConfirmationType) type switch
        {
            ConfirmationType.Trade => NebulaOperationTypes.Trade,
            ConfirmationType.MarketSellTransaction => NebulaOperationTypes.MarketListing,
            ConfirmationType.Purchase => NebulaOperationTypes.MarketPurchase,
            _ => null
        };
    }
}

/// <summary>Classifies transport exceptions of a Steam request.</summary>
public static class SteamErrorClassifier
{
    /// <summary>
    ///     The request certainly did not reach Steam: DNS, connect, proxy tunnel or TLS handshake failed (a send may be
    ///     retried). Anything else (timeout, reset after the request was written, HTTP error) is uncertain.
    /// </summary>
    public static bool IsBeforeWrite(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            return http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                or HttpRequestError.ProxyTunnelError or HttpRequestError.SecureConnectionError;
        }

        return ex.InnerException is SocketException {SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound};
    }

    /// <summary>proxy_failed when a configured proxy can't be used, otherwise steam_unavailable / rate_limited.</summary>
    public static string NetworkCode(Exception ex, bool proxyConfigured)
    {
        if (ex is HttpRequestException http)
        {
            if (http.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return NebulaResultCodes.RateLimited;
            if (proxyConfigured && http.HttpRequestError is HttpRequestError.ProxyTunnelError
                    or HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
                return NebulaResultCodes.ProxyFailed;
        }

        return NebulaResultCodes.SteamUnavailable;
    }
}
