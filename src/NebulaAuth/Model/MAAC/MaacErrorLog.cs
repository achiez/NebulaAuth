using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NebulaAuth.Utility;
using NLog;

namespace NebulaAuth.Model.MAAC;

/// <summary>
///     Log of auto-confirmation failures without a stack trace on every timer tick. Failures are grouped into series:
///     the same network / proxy error on the same proxy (any account), or the same other error of one account. The first
///     failure of a series is logged in full; repeats are only counted and summarized at most once per
///     <see cref="SummaryInterval" />; the end of a series (requests succeed again) is logged once. A different error
///     starts a new series, so it is logged in full. Logged text is passed through <see cref="LogSanitizer" />.
/// </summary>
public sealed partial class MaacErrorLog
{
    public static readonly TimeSpan DefaultSummaryInterval = TimeSpan.FromMinutes(10);

    private static MaacErrorLog? _instance;

    private readonly Func<DateTime> _clock;
    private readonly Logger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Series> _accountSeries = new(StringComparer.OrdinalIgnoreCase);

    public MaacErrorLog(Logger logger, TimeSpan summaryInterval, Func<DateTime>? clock = null)
    {
        _logger = logger;
        SummaryInterval = summaryInterval;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public static MaacErrorLog Instance => _instance ??= new MaacErrorLog(Shell.Logger, DefaultSummaryInterval);

    public TimeSpan SummaryInterval { get; }

    /// <summary>Series in progress (tests / diagnostics).</summary>
    public int ActiveSeries
    {
        get
        {
            lock (_lock) return _series.Count;
        }
    }

    /// <param name="account">Account name.</param>
    /// <param name="proxyLabel">Proxy used by the request ("proxy #3", "default proxy", "no proxy"); no credentials.</param>
    /// <param name="ex">The failure.</param>
    /// <param name="secrets">Values that must not appear in the log (proxy password...).</param>
    public void Failure(string account, string proxyLabel, Exception ex, IEnumerable<string?>? secrets = null)
    {
        var network = IsNetworkError(ex);
        var signature = GetSignature(ex, network);
        var key = network ? $"net|{proxyLabel}|{signature}" : $"acc|{account}|{signature}";
        var now = _clock();
        lock (_lock)
        {
            if (_accountSeries.TryGetValue(account, out var current) && current.Key != key)
            {
                Leave(current, account, false, now);
            }

            if (_series.TryGetValue(key, out var series))
            {
                series.Accounts.Add(account);
                _accountSeries[account] = series;
                series.Failures++;
                series.Unreported++;
                if (now - series.LastReport < SummaryInterval) return;
                _logger.Warn(
                    $"MAAC: {Describe(series)} is still failing: {series.Unreported} more failed request(s) in the last {FormatDuration(now - series.LastReport)}, {series.Failures} since {FormatDuration(now - series.Started)} ago; {AccountsText(series)}");
                series.Unreported = 0;
                series.LastReport = now;
                return;
            }

            series = new Series(key, network, proxyLabel, account, Short(ex, secrets), now);
            series.Accounts.Add(account);
            _series[key] = series;
            _accountSeries[account] = series;
            var details = LogSanitizer.Sanitize(ex.ToString(), secrets);
            var text =
                $"MAAC: {(network ? "network / proxy error" : "error")} for {account} via {proxyLabel}: {series.Error}. " +
                $"Repeats of this error are summarized every {FormatDuration(SummaryInterval)}.{Environment.NewLine}{details}";
            if (network) _logger.Warn(text);
            else _logger.Error(text);
        }
    }

    /// <summary>A request of <paramref name="account" /> succeeded.</summary>
    public void Success(string account)
    {
        lock (_lock)
        {
            if (_accountSeries.TryGetValue(account, out var series)) Leave(series, account, true, _clock());
        }
    }

    /// <summary>The account left the auto-confirmation: it no longer keeps its series open.</summary>
    public void Forget(string account)
    {
        lock (_lock)
        {
            if (!_accountSeries.TryGetValue(account, out var series)) return;
            _accountSeries.Remove(account);
            series.Accounts.Remove(account);
            if (series.Accounts.Count == 0) _series.Remove(series.Key);
        }
    }

    private void Leave(Series series, string account, bool recovered, DateTime now)
    {
        _accountSeries.Remove(account);
        series.Accounts.Remove(account);
        if (series.Accounts.Count > 0) return;
        _series.Remove(series.Key);
        var duration = FormatDuration(now - series.Started);
        if (recovered)
        {
            _logger.Info(
                $"MAAC: recovered, requests succeed again ({Describe(series)}; {series.Failures} failed request(s) over {duration})");
        }
        else
        {
            _logger.Info(
                $"MAAC: {Describe(series)} ended with another error ({series.Failures} failed request(s) over {duration})");
        }
    }

    private static string Describe(Series series)
    {
        return series.Network
            ? $"network / proxy error via {series.ProxyLabel} ({series.Error})"
            : $"error of {series.FirstAccount} via {series.ProxyLabel} ({series.Error})";
    }

    private static string AccountsText(Series series)
    {
        const int maxNames = 5;
        var names = series.Accounts.Take(maxNames).ToList();
        var more = series.Accounts.Count - names.Count;
        return $"accounts: {series.Accounts.Count} ({string.Join(", ", names)}{(more > 0 ? $", +{more}" : string.Empty)})";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 90) return $"{Math.Max(0, (int) span.TotalSeconds)} s";
        if (span.TotalMinutes < 90) return $"{(int) span.TotalMinutes} min";
        return $"{(int) span.TotalHours} h {span.Minutes} min";
    }

    /// <summary>
    ///     Network, proxy, TLS and timeout failures: expected while a proxy or the connection is down.
    /// </summary>
    public static bool IsNetworkError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException:
                case SocketException:
                case IOException:
                case TimeoutException:
                case AuthenticationException:
                // HttpClient timeout (a cancellation by the app itself is not a network error)
                case TaskCanceledException when e.InnerException is TimeoutException:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Identity of an error for grouping: exception types of the chain plus the network error codes, or (other errors)
    ///     also the message with numbers removed and the throwing method.
    /// </summary>
    public static string GetSignature(Exception ex, bool network)
    {
        var sb = new StringBuilder();
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append('>');
            sb.Append(e.GetType().Name);
            switch (e)
            {
                case HttpRequestException http:
                    sb.Append('[').Append(http.HttpRequestError).Append(',').Append((int?) http.StatusCode).Append(']');
                    break;
                case SocketException socket:
                    sb.Append('[').Append(socket.SocketErrorCode).Append(']');
                    break;
            }

            if (!network)
            {
                sb.Append(':').Append(Digits().Replace(e.Message, "#"));
                if (e.TargetSite != null) sb.Append('@').Append(e.TargetSite.DeclaringType?.Name).Append('.').Append(e.TargetSite.Name);
            }
        }

        return sb.ToString();
    }

    private static string Short(Exception ex, IEnumerable<string?>? secrets)
    {
        var inner = ex.GetBaseException();
        var text = inner == ex
            ? Describe(ex)
            : $"{Describe(ex)} -> {Describe(inner)}";
        return LogSanitizer.Sanitize(text, secrets);

        // Error codes too: the messages are localized by the OS
        static string Describe(Exception e)
        {
            var code = e switch
            {
                HttpRequestException http => $" [{http.HttpRequestError}{(http.StatusCode != null ? $", {(int) http.StatusCode}" : string.Empty)}]",
                SocketException socket => $" [{socket.SocketErrorCode}]",
                _ => string.Empty
            };
            return $"{e.GetType().Name}{code}: {e.Message.TrimEnd('.', ' ')}";
        }
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    private sealed class Series(string key, bool network, string proxyLabel, string firstAccount, string error, DateTime started)
    {
        public string Key { get; } = key;
        public bool Network { get; } = network;
        public string ProxyLabel { get; } = proxyLabel;
        public string FirstAccount { get; } = firstAccount;
        public string Error { get; } = error;
        public DateTime Started { get; } = started;
        public DateTime LastReport { get; set; } = started;
        public int Failures { get; set; } = 1;
        public int Unreported { get; set; }
        public HashSet<string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
