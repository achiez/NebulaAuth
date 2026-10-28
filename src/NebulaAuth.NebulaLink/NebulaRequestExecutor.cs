using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Protocol;
using SteamLib;

namespace NebulaAuth.NebulaLink;

/// <summary>Local state the executor checks before every command (a seam for tests).</summary>
public interface INebulaExecutorContext
{
    bool IsPaused { get; }
    NebulaAccountLink? GetAccountLink(int accountId);

    /// <summary>Steam rejected the saved password of the account: no automatic login until the user fixes it.</summary>
    bool IsInvalidPassword(MobileDataExtended mafile);

    /// <summary>The local accounts, one mafile per login (duplicate mafiles resolved).</summary>
    LocalAccounts LocalAccounts();

    /// <summary>The current server module catalog: a confirm must name one of its entries.</summary>
    NebulaModuleCatalog Catalog { get; }

    /// <summary>check_ready: an account state (<see cref="NebulaAccountStates" />) without any Steam call.</summary>
    string CheckReady(MobileDataExtended mafile);
}

/// <summary>
///     Validates and runs Achiefy commands (§5.5): dedupe by request id, deadline, command shape and category, pause,
///     local account link and its mafile (by login), the (category, module) grant of a confirm (checked
///     again right before the send; the module must be in the current catalog), then
///     login_code / check_confirmation / confirm / check_ready. At most <see cref="MaxInFlight" /> commands run at once
///     (the server's Hello limit); confirmation reads and sends of one account are serialized by
///     <see cref="ConfirmationService" />.
/// </summary>
public sealed class NebulaRequestExecutor
{
    public const int MAX_TTL_MS = 120_000;
    /// <summary>Concurrent commands until the server announced its limit.</summary>
    public const int DEFAULT_MAX_IN_FLIGHT = 8;

    /// <summary>Upper bound of the server's limit the device accepts.</summary>
    public const int MAX_IN_FLIGHT_CAP = 64;

    public const int MAX_CONFIRM_IDS = 50;
    public static readonly TimeSpan DedupeLifetime = TimeSpan.FromMinutes(15);

    private readonly Func<DateTimeOffset> _clock;
    private readonly ConfirmationService _confirmations;
    private readonly INebulaExecutorContext _context;
    private readonly ConcurrentDictionary<Guid, RequestEntry> _entries = new();
    private readonly GuardCodeService _guard;
    private readonly NebulaLinkHistoryStore _history;
    private readonly Func<NebulaCommandResult, Task> _send;
    private readonly RequestSlots _slots = new(DEFAULT_MAX_IN_FLIGHT);

    public NebulaRequestExecutor(INebulaExecutorContext context, GuardCodeService guard,
        ConfirmationService confirmations, NebulaLinkHistoryStore history, Func<NebulaCommandResult, Task> send,
        Func<DateTimeOffset>? clock = null)
    {
        _context = context;
        _guard = guard;
        _confirmations = confirmations;
        _history = history;
        _send = send;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Commands that run at once; as many more may wait for a slot.</summary>
    public int MaxInFlight => _slots.Limit;

    /// <summary>Hello <c>limits.maxInFlight</c>: the server delivers at most that many unanswered requests.</summary>
    public void SetMaxInFlight(int value)
    {
        _slots.SetLimit(value <= 0 ? DEFAULT_MAX_IN_FLIGHT : Math.Min(value, MAX_IN_FLIGHT_CAP));
    }

    /// <summary>A command finished (history updated): the UI refreshes "last request".</summary>
    public event Action<NebulaCommand, NebulaCommandResult>? Completed;

    /// <summary>Hub handler: runs the command in the background (never blocks the connection).</summary>
    public void Execute(NebulaCommand command)
    {
        _ = Task.Run(() => ExecuteAsync(command));
    }

    /// <summary>
    ///     Runs the command and sends the result. A duplicate of a running request is ignored (null); a duplicate of a
    ///     finished one re-sends the stored result.
    /// </summary>
    public async Task<NebulaCommandResult?> ExecuteAsync(NebulaCommand command)
    {
        Prune();
        var receivedAt = _clock();
        var entry = new RequestEntry(receivedAt);
        var existing = _entries.GetOrAdd(command.RequestId, entry);
        if (!ReferenceEquals(existing, entry))
        {
            if (existing.Result == null)
            {
                NebulaLinkLog.Debug($"Request {command.RequestId}: duplicate while in progress, ignored");
                return null;
            }

            NebulaLinkLog.Debug($"Request {command.RequestId}: duplicate, stored result re-sent");
            await SafeSend(existing.Result);
            return existing.Result;
        }

        NebulaCommandResult result;
        MobileDataExtended? mafile = null;
        var entered = false;
        try
        {
            var slot = await _slots.EnterAsync(SlotWaitLimit(command, receivedAt), entry.Cancellation.Token);
            if (slot == SlotResult.Entered)
            {
                entered = true;
                (result, mafile) = await RunAsync(command, receivedAt, entry.Cancellation.Token);
            }
            else
            {
                // Never started (nothing went to Steam): asking again is safe
                result = Result(command, slot == SlotResult.Full ? NebulaResultCodes.Busy : NebulaResultCodes.Expired);
            }
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            result = Result(command, entry.CancelReason ?? NebulaResultCodes.Cancelled);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Error($"Request {command.RequestId} ({command.Type}) failed", ex);
            result = Result(command, NebulaResultCodes.InternalError);
            result.Diagnostic = NebulaLinkLog.Diagnostic(ex);
        }
        finally
        {
            if (entered) _slots.Release();
        }

        entry.Result = result;
        Record(command, result, mafile);
        NebulaLinkLog.Info(
            $"Request {command.RequestId}: account {command.AccountId}, {command.ModuleId}, {command.Type} -> result {result.Code}" +
            (result.Items != null ? $" [{string.Join(",", result.Items.Select(i => i.Code))}]" : ""));
        Completed?.Invoke(command, result);
        await SafeSend(result);
        return result;
    }

    /// <summary>CancelCommand: effective only before a Steam send started.</summary>
    public void Cancel(Guid requestId, string? reason)
    {
        if (!_entries.TryGetValue(requestId, out var entry) || entry.Result != null) return;
        entry.CancelReason = reason is NebulaResultCodes.NotPaired or NebulaResultCodes.DevicePaused
            or NebulaResultCodes.Expired
            ? reason
            : NebulaResultCodes.Cancelled;
        entry.Cancellation.Cancel();
    }

    /// <summary>Cancels every running command (link revoked / replaced / paused).</summary>
    public void CancelAll(string reason)
    {
        foreach (var (id, entry) in _entries)
        {
            if (entry.Result == null) Cancel(id, reason);
        }
    }

    private async Task<(NebulaCommandResult, MobileDataExtended?)> RunAsync(NebulaCommand cmd, DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        // 2. Shape, deadline, scope derivation
        var validation = Validate(cmd);
        if (validation != null) return (Result(cmd, validation), null);
        var deadline = receivedAt + TimeSpan.FromMilliseconds(cmd.TtlMs);
        if (_clock() >= deadline) return (Result(cmd, NebulaResultCodes.Expired), null);

        // 3. Pause
        if (_context.IsPaused) return (Result(cmd, NebulaResultCodes.DevicePaused), null);

        // 4. Local account link, mafile (one per login: duplicate mafiles the user did not resolve are not served)
        var link = _context.GetAccountLink(cmd.AccountId);
        if (link == null) return (Result(cmd, NebulaResultCodes.AccountUnlinked), null);
        var local = _context.LocalAccounts();
        if (!local.ByLogin.TryGetValue(link.Login, out var mafile))
        {
            return local.IsUnresolved(link.Login)
                ? AccountState(cmd, NebulaAccountStates.MatchConflict, null)
                : AccountState(cmd, NebulaAccountStates.MafileMissing, null);
        }

        if (!string.IsNullOrEmpty(cmd.Login) && AccountMatcher.Normalize(cmd.Login) != link.Login)
            return AccountState(cmd, NebulaAccountStates.MatchConflict, mafile);

        // 5. Grant: only a confirm needs one, for a (category, module) of the current catalog
        var grant = cmd.Category != null ? new GrantKey(cmd.Category, cmd.ModuleId) : null;
        if (cmd.Type == NebulaCommandTypes.Confirm && !GrantOn(cmd.AccountId, link.Login, grant!))
            return (Result(cmd, NebulaResultCodes.ScopeDenied), mafile);

        ct.ThrowIfCancellationRequested();
        // A rejected stored password stops every session-requiring command until the user fixes it
        if (cmd.Type is NebulaCommandTypes.CheckConfirmation or NebulaCommandTypes.Confirm &&
            _context.IsInvalidPassword(mafile))
            return AccountState(cmd, NebulaAccountStates.InvalidLocalPassword, mafile);

        // 6. Dispatch
        switch (cmd.Type)
        {
            case NebulaCommandTypes.LoginCode:
            {
                // The code may have to wait for an unused window (one code per login); never past the deadline
                using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var left = deadline - _clock();
                if (left <= TimeSpan.Zero) return (Result(cmd, NebulaResultCodes.Expired), mafile);
                deadlineCts.CancelAfter(left);
                string code;
                int validFor;
                try
                {
                    (code, validFor) = await _guard.GetCodeAsync(AccountSteamClientPool.KeyOf(mafile), mafile.SharedSecret,
                        deadlineCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (Result(cmd, NebulaResultCodes.Expired), mafile);
                }

                var result = Result(cmd, NebulaResultCodes.Ok);
                result.GuardCode = code;
                result.ValidForSeconds = validFor;
                return (result, mafile);
            }
            case NebulaCommandTypes.CheckReady:
            {
                var state = _context.CheckReady(mafile);
                var result = Result(cmd, state == NebulaAccountStates.Ready ? NebulaResultCodes.Ok : state);
                result.AccountState = state;
                return (result, mafile);
            }
            case NebulaCommandTypes.CheckConfirmation:
            {
                var type = ConfirmationTypes.FromOperationType(cmd.Operation!.Type)!.Value;
                var id = ulong.Parse(cmd.Operation.Ids[0], CultureInfo.InvariantCulture);
                var outcome = await _confirmations.CheckAsync(mafile, type, id, receivedAt, ct);
                var result = Result(cmd, outcome.Code);
                result.Resolution = outcome.Resolution;
                result.Diagnostic = outcome.Diagnostic;
                if (IsAccountStateCode(outcome.Code)) result.AccountState = outcome.Code;
                return (result, mafile);
            }
            case NebulaCommandTypes.Confirm:
            {
                var type = ConfirmationTypes.FromOperationType(cmd.Operation!.Type)!.Value;
                var ids = cmd.Operation.Ids.Select(i => ulong.Parse(i, CultureInfo.InvariantCulture)).ToList();
                var outcome = await _confirmations.ConfirmForAchiefyAsync(mafile, type, ids,
                    cmd.Decision == NebulaDecisions.Accept, () => GrantOn(cmd.AccountId, link.Login, grant!),
                    cmd.RequestId, receivedAt, deadline, ct);
                var result = Result(cmd, outcome.Code);
                result.Items = outcome.Items?.ToArray();
                result.Diagnostic = outcome.Diagnostic;
                if (IsAccountStateCode(outcome.Code)) result.AccountState = outcome.Code;
                return (result, mafile);
            }
            default:
                return (Result(cmd, NebulaResultCodes.Unsupported), mafile);
        }
    }

    /// <summary>Null when the command is well-formed; otherwise invalid_request / unsupported.</summary>
    public static string? Validate(NebulaCommand cmd)
    {
        if (cmd.RequestId == Guid.Empty || cmd.AccountId <= 0) return NebulaResultCodes.InvalidRequest;
        if (cmd.TtlMs <= 0 || cmd.TtlMs > MAX_TTL_MS) return NebulaResultCodes.InvalidRequest;
        // Categories are fixed in the protocol: a new one needs a NebulaAuth update
        if (cmd.Category != null && !ModuleCatalogs.IsKnownCategory(cmd.Category)) return NebulaResultCodes.Unsupported;
        if (!ModuleCatalogs.IsValidId(cmd.ModuleId)) return NebulaResultCodes.InvalidRequest;
        switch (cmd.Type)
        {
            case NebulaCommandTypes.LoginCode:
            case NebulaCommandTypes.CheckReady:
                return cmd.Category == null && cmd.Operation == null && cmd.Decision == null
                    ? null
                    : NebulaResultCodes.InvalidRequest;
            case NebulaCommandTypes.CheckConfirmation:
            {
                if (cmd.Decision != null) return NebulaResultCodes.InvalidRequest;
                var op = OperationCode(cmd.Operation);
                if (op != null) return op;
                if (cmd.Category != NebulaCategories.FromOperationType(cmd.Operation!.Type)) return NebulaResultCodes.InvalidRequest;
                return cmd.Operation.Ids.Length == 1 ? null : NebulaResultCodes.InvalidRequest;
            }
            case NebulaCommandTypes.Confirm:
            {
                var op = OperationCode(cmd.Operation);
                if (op != null) return op;
                if (cmd.Decision is not (NebulaDecisions.Accept or NebulaDecisions.Decline))
                    return NebulaResultCodes.InvalidRequest;
                var ids = cmd.Operation!.Ids;
                var max = cmd.Operation.Type == NebulaOperationTypes.MarketListing ? MAX_CONFIRM_IDS : 1;
                if (ids.Length < 1 || ids.Length > max) return NebulaResultCodes.InvalidRequest;
                // The category follows from the operation; a server string that does not match is refused
                return cmd.Category == NebulaCategories.FromOperationType(cmd.Operation.Type)
                    ? null
                    : NebulaResultCodes.InvalidRequest;
            }
            default:
                return NebulaResultCodes.Unsupported;
        }

        static string? OperationCode(NebulaOperation? op)
        {
            if (op == null || op.Ids == null) return NebulaResultCodes.InvalidRequest;
            if (ConfirmationTypes.FromOperationType(op.Type) == null) return NebulaResultCodes.Unsupported;
            foreach (var id in op.Ids)
            {
                if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
                    return NebulaResultCodes.InvalidRequest;
            }

            return op.Ids.Distinct().Count() == op.Ids.Length ? null : NebulaResultCodes.InvalidRequest;
        }
    }

    private bool GrantOn(int accountId, string login, GrantKey grant)
    {
        // Read again every time: the user may have turned it off a moment ago, the server may have dropped the module
        if (!ModuleCatalogs.Contains(_context.Catalog, grant)) return false;
        var link = _context.GetAccountLink(accountId);
        return link != null && link.Login == login && link.HasGrant(grant);
    }

    private static bool IsAccountStateCode(string code)
    {
        return code is NebulaAccountStates.SteamSessionRequired
            or NebulaAccountStates.InvalidLocalPassword or NebulaAccountStates.MafileMissing or NebulaAccountStates.MatchConflict;
    }

    private static (NebulaCommandResult, MobileDataExtended?) AccountState(NebulaCommand cmd, string state, MobileDataExtended? mafile)
    {
        var result = Result(cmd, state);
        result.AccountState = state;
        return (result, mafile);
    }

    private static NebulaCommandResult Result(NebulaCommand cmd, string code)
    {
        return new NebulaCommandResult {RequestId = cmd.RequestId, Code = code};
    }

    private void Record(NebulaCommand cmd, NebulaCommandResult result, MobileDataExtended? mafile)
    {
        try
        {
            var login = mafile != null ? AccountSteamClientPool.KeyOf(mafile) : null;
            _history.AddRequest(new RequestRecord
            {
                RequestId = cmd.RequestId,
                Time = _clock(),
                AccountId = cmd.AccountId,
                Login = login,
                Module = cmd.ModuleId,
                Category = cmd.Category,
                Type = cmd.Type,
                OperationType = cmd.Operation?.Type,
                IdCount = cmd.Operation?.Ids?.Length ?? 0,
                Ids = cmd.Operation?.Ids?.ToArray(),
                Decision = cmd.Decision,
                Result = result.Items is {Length: > 0} items && result.Code == NebulaResultCodes.Ok
                    ? SummarizeItems(items)
                    : result.Code
            });
            if (login != null && cmd.Operation != null &&
                ConfirmationTypes.FromOperationType(cmd.Operation.Type) is { } type &&
                cmd.Type is NebulaCommandTypes.CheckConfirmation or NebulaCommandTypes.Confirm)
            {
                foreach (var id in cmd.Operation.Ids.Distinct())
                    _history.TouchKnownOperation(login, cmd.AccountId, type, id);
            }
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Request history can't be updated", ex);
        }
    }

    /// <summary>One code when every item has it, otherwise the most important one (uncertain, rejected, not found).</summary>
    private static string SummarizeItems(NebulaOperationResult[] items)
    {
        var codes = items.Select(i => i.Code).Distinct().ToList();
        if (codes.Count == 1) return codes[0];
        foreach (var code in new[]
                 {
                     NebulaResultCodes.ResultUnknown, NebulaResultCodes.SteamRejected, NebulaResultCodes.NotFound
                 })
        {
            if (codes.Contains(code)) return code;
        }

        return codes[0];
    }

    private async Task SafeSend(NebulaCommandResult result)
    {
        try
        {
            await _send(result);
        }
        catch (Exception ex)
        {
            // Kept in the dedupe map: the server redelivers the command after reconnect and gets it again
            NebulaLinkLog.Warn($"Result of request {result.RequestId} not delivered now", ex);
        }
    }

    private void Prune()
    {
        var cutoff = _clock() - DedupeLifetime;
        foreach (var (id, entry) in _entries)
        {
            if (entry.Result != null && entry.ReceivedAt < cutoff) _entries.TryRemove(id, out _);
        }
    }

    /// <summary>A command waits for a slot at most until its deadline.</summary>
    private TimeSpan SlotWaitLimit(NebulaCommand command, DateTimeOffset receivedAt)
    {
        return receivedAt + TimeSpan.FromMilliseconds(Math.Clamp(command.TtlMs, 0, MAX_TTL_MS)) - _clock();
    }

    private enum SlotResult
    {
        Entered,
        Full,
        TimedOut
    }

    /// <summary>
    ///     Runs at most <see cref="Limit" /> commands; up to <see cref="Limit" /> more wait in arrival order, beyond that a
    ///     command is refused at once. The limit can change while commands run.
    /// </summary>
    private sealed class RequestSlots(int limit)
    {
        private readonly object _lock = new();
        private readonly LinkedList<TaskCompletionSource> _waiting = new();
        private int _running;

        public int Limit { get; private set; } = limit;

        public void SetLimit(int limit)
        {
            lock (_lock)
            {
                Limit = limit;
                GrantLocked();
            }
        }

        public async Task<SlotResult> EnterAsync(TimeSpan maxWait, CancellationToken ct)
        {
            LinkedListNode<TaskCompletionSource> node;
            lock (_lock)
            {
                if (_running < Limit && _waiting.Count == 0)
                {
                    _running++;
                    return SlotResult.Entered;
                }

                if (_waiting.Count >= Limit) return SlotResult.Full;
                node = _waiting.AddLast(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(maxWait > TimeSpan.Zero ? maxWait : TimeSpan.Zero);
            try
            {
                await node.Value.Task.WaitAsync(timeout.Token);
                return SlotResult.Entered;
            }
            catch (OperationCanceledException)
            {
                bool granted;
                lock (_lock)
                {
                    granted = node.List == null;
                    if (!granted) _waiting.Remove(node);
                }

                // Granted at the same moment: the slot goes to the next one
                if (granted) Release();
                ct.ThrowIfCancellationRequested();
                return SlotResult.TimedOut;
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                _running--;
                GrantLocked();
            }
        }

        private void GrantLocked()
        {
            while (_running < Limit && _waiting.First is { } first)
            {
                _waiting.RemoveFirst();
                _running++;
                first.Value.TrySetResult();
            }
        }
    }

    private sealed class RequestEntry(DateTimeOffset receivedAt)
    {
        public DateTimeOffset ReceivedAt { get; } = receivedAt;
        public CancellationTokenSource Cancellation { get; } = new();
        public volatile NebulaCommandResult? Result;
        public volatile string? CancelReason;
    }
}
