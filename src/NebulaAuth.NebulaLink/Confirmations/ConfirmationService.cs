using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Protocol;
using SteamLib;
using SteamLib.SteamMobile.Confirmations;

namespace NebulaAuth.NebulaLink.Confirmations;

/// <summary>What the confirmation service needs to know about Achiefy account links.</summary>
public interface IConfirmationLinkInfo
{
    /// <summary>Achiefy account id of a linked account (by account key = normalized login), null when not linked.</summary>
    int? GetLinkedAccountId(string accountKey);
}

/// <summary>
///     Mobile confirmation reads and sends of the manual screen and of Achiefy requests (the auto-confirmer, which never
///     runs for linked accounts, uses its own client): an in-memory snapshot per account, coalesced reads (one Steam request for concurrent readers), a minimum
///     interval between reads, one operation at a time per account, and a persisted decision history (no nonce, no
///     content). Negative answers for Achiefy come only from a list fetched after the request arrived
/// </summary>
public sealed class ConfirmationService
{
    public static readonly TimeSpan PositiveReuse = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinFetchInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ConfirmFreshness = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan UncertainRetryDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MinRemainingBeforeSend = TimeSpan.FromSeconds(3);
    public const int GLOBAL_FETCH_LIMIT = 8;
    public const int PER_PROXY_FETCH_LIMIT = 2;

    private readonly ConcurrentDictionary<string, AccountEntry> _accounts = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _globalFetch = new(GLOBAL_FETCH_LIMIT, GLOBAL_FETCH_LIMIT);
    private readonly IConfirmationLinkInfo _links;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _proxyFetch = new();
    private readonly IConfirmationTransport _transport;

    public ConfirmationService(IConfirmationTransport transport, NebulaLinkHistoryStore history,
        IConfirmationLinkInfo links, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _transport = transport;
        History = history;
        _links = links;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public NebulaLinkHistoryStore History { get; }

    /// <summary>
    ///     A manual accept/decline of an operation that an Achiefy request referenced within 24 h queued user action events
    ///     in the history (to be sent).
    /// </summary>
    public event Action? EventsQueued;

    #region Reads

    /// <summary>The list of the account under <paramref name="policy" /> (reused snapshot or one coalesced fetch).</summary>
    public async Task<SnapshotResult> GetAsync(MobileDataExtended mafile, SnapshotPolicy policy, SessionMode mode,
        CancellationToken ct)
    {
        var entry = Entry(mafile);
        await entry.Gate.WaitAsync(ct);
        try
        {
            var (snapshot, _) = await GetUnderGate(entry, mafile, policy, mode, ct);
            return new SnapshotResult(snapshot.Items.ToList(), snapshot.FetchedAt);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    ///     check_confirmation: pending when the exact (type, CreatorId) is in a snapshot at most 10 s old; not_found only
    ///     from a snapshot whose fetch started after <paramref name="receivedAt" /> (+ the resolution of a local decision).
    /// </summary>
    public async Task<ConfirmationCheckOutcome> CheckAsync(MobileDataExtended mafile, int type, ulong creatorId,
        DateTimeOffset receivedAt, CancellationToken ct)
    {
        var key = AccountSteamClientPool.KeyOf(mafile);
        var entry = Entry(mafile);
        await entry.Gate.WaitAsync(ct);
        try
        {
            var now = _clock();
            var snapshot = entry.Snapshot;
            if (snapshot != null && now - snapshot.FetchedAt <= PositiveReuse && Contains(snapshot, type, creatorId))
                return new ConfirmationCheckOutcome(NebulaResultCodes.Pending);

            try
            {
                (snapshot, _) = await GetUnderGate(entry, mafile, new SnapshotPolicy(PositiveReuse, receivedAt),
                    SessionMode.Headless, ct);
            }
            catch (NebulaCodeException ex)
            {
                return new ConfirmationCheckOutcome(ex.Code, null, ex.Message);
            }

            return Contains(snapshot, type, creatorId)
                ? new ConfirmationCheckOutcome(NebulaResultCodes.Pending)
                : new ConfirmationCheckOutcome(NebulaResultCodes.NotFound, ResolutionOf(key, type, creatorId));
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    #endregion

    #region Achiefy confirm

    /// <summary>
    ///     confirm for Achiefy: answers from the decision history first (no Steam call), then a fresh read, exact match by
    ///     (type, CreatorId), a grant check right before the send, one send, history. Never sends an operation that is
    ///     not in a list fetched after the request arrived.
    /// </summary>
    public async Task<ConfirmOutcome> ConfirmForAchiefyAsync(MobileDataExtended mafile, int type, IReadOnlyList<ulong> ids,
        bool accept, Func<bool> grantStillOn, Guid requestId, DateTimeOffset receivedAt, DateTimeOffset deadline,
        CancellationToken ct)
    {
        var key = AccountSteamClientPool.KeyOf(mafile);
        var results = new Dictionary<ulong, NebulaOperationResult>();
        var pending = new List<ulong>();
        foreach (var id in ids.Distinct())
        {
            var decision = History.FindDecision(key, type, Id(id));
            if (decision != null)
                results[id] = Item(id, decision.Accepted ? NebulaResultCodes.Confirmed : NebulaResultCodes.Declined,
                    decision.Resolution);
            else pending.Add(id);
        }

        if (pending.Count == 0) return Ok();

        var entry = Entry(mafile);
        await entry.Gate.WaitAsync(ct);
        try
        {
            // After an uncertain send, the list is read again no earlier than 5 s later
            var waitUntil = pending.Select(id => History.FindUncertain(key, type, Id(id))?.SentAt + UncertainRetryDelay)
                .Where(t => t != null).Select(t => t!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            var now = _clock();
            if (waitUntil > now)
            {
                if (deadline - waitUntil < MinRemainingBeforeSend) return Top(NebulaResultCodes.Expired);
                await _delay(waitUntil - now, ct);
            }

            if (deadline - _clock() < MinRemainingBeforeSend) return Top(NebulaResultCodes.Expired);

            ConfirmationSnapshot snapshot;
            try
            {
                (snapshot, _) = await GetUnderGate(entry, mafile, new SnapshotPolicy(ConfirmFreshness, receivedAt),
                    SessionMode.Headless, ct);
            }
            catch (NebulaCodeException ex)
            {
                return Top(ex.Code, ex.Message);
            }

            var found = new List<Confirmation>();
            foreach (var id in pending)
            {
                var match = snapshot.Items.FirstOrDefault(c => (int) c.ConfType == type && c.CreatorId == id);
                if (match != null) found.Add(match);
                else
                    results[id] = Item(id, NebulaResultCodes.NotFound,
                        History.FindUncertain(key, type, Id(id)) != null
                            ? NebulaResolutions.UnknownAfterSend
                            : ResolutionOf(key, type, id));
            }

            if (found.Count == 0) return Ok();
            // The grant may have been turned off while the list was read
            if (!grantStillOn()) return Top(NebulaResultCodes.ScopeDenied);
            if (deadline - _clock() < MinRemainingBeforeSend) return Top(NebulaResultCodes.Expired);

            var send = await _transport.SendAsync(mafile, found, accept, SessionMode.Headless, CancellationToken.None);
            entry.Snapshot = null;
            switch (send.Status)
            {
                case TransportSendStatus.Success:
                    foreach (var c in found)
                    {
                        results[c.CreatorId] = Item(c.CreatorId,
                            accept ? NebulaResultCodes.Confirmed : NebulaResultCodes.Declined, null);
                        History.AddDecision(new DecisionRecord
                        {
                            Login = key, Type = type, CreatorId = Id(c.CreatorId),
                            Decision = accept ? NebulaDecisions.Accept : NebulaDecisions.Decline,
                            Origin = DecisionRecord.ORIGIN_ACHIEFY, RequestId = requestId, Time = _clock()
                        });
                    }

                    break;
                case TransportSendStatus.Rejected:
                    foreach (var c in found) results[c.CreatorId] = Item(c.CreatorId, NebulaResultCodes.SteamRejected, null);
                    break;
                case TransportSendStatus.Unknown:
                    foreach (var c in found)
                    {
                        results[c.CreatorId] = Item(c.CreatorId, NebulaResultCodes.ResultUnknown, null);
                        History.AddUncertain(key, type, Id(c.CreatorId));
                    }

                    break;
                default:
                    return Top(send.Code ?? NebulaResultCodes.SteamUnavailable, send.Diagnostic);
            }

            return Ok();
        }
        finally
        {
            entry.Gate.Release();
        }

        ConfirmOutcome Ok()
        {
            return new ConfirmOutcome(NebulaResultCodes.Ok, ids.Distinct().Select(id => results[id]).ToList());
        }

        static ConfirmOutcome Top(string code, string? diagnostic = null)
        {
            return new ConfirmOutcome(code, null, diagnostic);
        }
    }

    #endregion

    #region Manual

    /// <summary>
    ///     A manual accept/decline from the main screen (same per-account serialization as Achiefy requests). Exceptions
    ///     are thrown as they are (the screen shows them); an exception after the request may have reached Steam is
    ///     recorded as uncertain for linked accounts.
    /// </summary>
    public async Task<bool> SendManualAsync(MobileDataExtended mafile, IReadOnlyList<Confirmation> items, bool accept,
        CancellationToken ct)
    {
        if (items.Count == 0) return false;
        var key = AccountSteamClientPool.KeyOf(mafile);
        var accountId = _links.GetLinkedAccountId(key);
        var entry = Entry(mafile);
        await entry.Gate.WaitAsync(ct);
        try
        {
            TransportSendResult result;
            try
            {
                result = await _transport.SendAsync(mafile, items, accept, SessionMode.Interactive, ct);
            }
            catch (Exception ex)
            {
                entry.Snapshot = null;
                if (accountId != null && !SteamErrorClassifier.IsBeforeWrite(ex))
                {
                    foreach (var c in items.Where(c => ConfirmationTypes.ToOperationType((int) c.ConfType) != null))
                        History.AddUncertain(key, (int) c.ConfType, Id(c.CreatorId));
                }

                throw;
            }

            entry.Snapshot = null;
            if (result.Status != TransportSendStatus.Success) return false;
            if (accountId != null) RecordManualDecisions(mafile, key, accountId.Value, items, accept);
            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void RecordManualDecisions(MobileDataExtended mafile, string key, int accountId, IReadOnlyList<Confirmation> items,
        bool accept)
    {
        var reported = new List<Confirmation>();
        foreach (var c in items)
        {
            var type = (int) c.ConfType;
            var operationType = ConfirmationTypes.ToOperationType(type);
            if (operationType == null) continue;
            var id = Id(c.CreatorId);
            History.AddDecision(new DecisionRecord
            {
                Login = key, Type = type, CreatorId = id,
                Decision = accept ? NebulaDecisions.Accept : NebulaDecisions.Decline,
                Origin = DecisionRecord.ORIGIN_MANUAL, Time = _clock()
            });
            var known = History.FindKnownOperation(key, type, id);
            if (known == null) continue;
            History.QueueEvent(new UserActionEvent
            {
                EventId = Guid.NewGuid(),
                AccountId = known.AccountId != 0 ? known.AccountId : accountId,
                Action = accept ? NebulaUserActions.ConfirmedByUser : NebulaUserActions.DeclinedByUser,
                Operation = new NebulaOperation {Type = operationType, Ids = [id]},
                OccurredAt = _clock()
            });
            reported.Add(c);
        }

        if (reported.Count > 0) EventsQueued?.Invoke();
    }

    #endregion

    /// <summary>Forgets the snapshot of an account (send, session refresh, proxy change, unlink).</summary>
    public void Invalidate(string accountKey)
    {
        if (_accounts.TryGetValue(accountKey, out var entry)) entry.Snapshot = null;
    }

    #region Internals

    private AccountEntry Entry(MobileDataExtended mafile)
    {
        return _accounts.GetOrAdd(AccountSteamClientPool.KeyOf(mafile), _ => new AccountEntry());
    }

    // Runs under the account gate
    private async Task<(ConfirmationSnapshot Snapshot, bool Cached)> GetUnderGate(AccountEntry entry, MobileDataExtended mafile,
        SnapshotPolicy policy, SessionMode mode, CancellationToken ct)
    {
        var now = _clock();
        var snapshot = entry.Snapshot;
        if (snapshot != null && policy.Accepts(snapshot, now)) return (snapshot, true);

        var wait = entry.LastFetchStartedAt + MinFetchInterval - now;
        if (wait > TimeSpan.Zero) await _delay(wait, ct);

        var proxyGate = _proxyFetch.GetOrAdd(SafeProxyKey(mafile), _ => new SemaphoreSlim(PER_PROXY_FETCH_LIMIT));
        await _globalFetch.WaitAsync(ct);
        try
        {
            await proxyGate.WaitAsync(ct);
            try
            {
                var startedAt = _clock();
                entry.LastFetchStartedAt = startedAt;
                // An error leaves the previous snapshot out: errors are never cached as an empty list
                entry.Snapshot = null;
                var items = await _transport.FetchAsync(mafile, mode, ct);
                var fresh = new ConfirmationSnapshot(startedAt, _clock(), items.ToList());
                entry.Snapshot = fresh;
                return (fresh, false);
            }
            finally
            {
                proxyGate.Release();
            }
        }
        finally
        {
            _globalFetch.Release();
        }
    }

    private string SafeProxyKey(MobileDataExtended mafile)
    {
        try
        {
            return _transport.ProxyKey(mafile);
        }
        catch
        {
            return "direct";
        }
    }

    private static bool Contains(ConfirmationSnapshot snapshot, int type, ulong creatorId)
    {
        return snapshot.Items.Any(c => (int) c.ConfType == type && c.CreatorId == creatorId);
    }

    private string? ResolutionOf(string key, int type, ulong creatorId)
    {
        var id = Id(creatorId);
        var decision = History.FindDecision(key, type, id);
        if (decision != null) return decision.Resolution;
        return History.FindUncertain(key, type, id) != null ? NebulaResolutions.UnknownAfterSend : null;
    }

    private static string Id(ulong id)
    {
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static NebulaOperationResult Item(ulong id, string code, string? resolution)
    {
        return new NebulaOperationResult {Id = Id(id), Code = code, Resolution = resolution};
    }

    private sealed class AccountEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public volatile ConfirmationSnapshot? Snapshot;
        public DateTimeOffset LastFetchStartedAt = DateTimeOffset.MinValue;
    }

    #endregion
}
