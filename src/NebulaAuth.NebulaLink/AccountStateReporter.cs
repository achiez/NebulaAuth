using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Protocol;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Reports the states and grants of the linked accounts to the server: a full report after Hello, a delta for given
///     accounts, and the accounts whose state changed locally (sessions, mafiles, saved passwords; a burst of local changes
///     is coalesced, and sessions are also checked once a minute because they expire with time).
/// </summary>
internal sealed class AccountStateReporter
{
    private static readonly TimeSpan LocalChangeDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LocalCheckInterval = TimeSpan.FromMinutes(1);

    private readonly Func<NebulaConnection?> _connection;

    // The states the server got last (account id → state and grants)
    private readonly Dictionary<int, string> _reported = new();
    private readonly Func<IReadOnlyCollection<int>?, List<AccountStateItem>> _states;
    private Timer? _checkTimer;
    private int _localChangeQueued;

    /// <param name="connection">The current connection (null when none).</param>
    /// <param name="states">The current states of the linked accounts (all when null, else of these account ids).</param>
    public AccountStateReporter(Func<NebulaConnection?> connection, Func<IReadOnlyCollection<int>?, List<AccountStateItem>> states)
    {
        _connection = connection;
        _states = states;
    }

    /// <summary>After Hello: the complete set (the server deletes the rows not reported); starts the periodic check.</summary>
    public async Task ReportAllAsync(CancellationToken ct)
    {
        var connection = _connection();
        if (connection is not {IsConnected: true}) return;
        var batches = _states(null).Chunk(ServerAccountMirror.PAGE_SIZE).ToList();
        if (batches.Count == 0) batches.Add([]);
        lock (_reported) _reported.Clear();
        for (var i = 0; i < batches.Count; i++)
        {
            await connection.InvokeAsync<AckResponse>("ReportAccountStates", new AccountStateReport
            {
                Full = true, FullBatchIndex = i, FullBatchCount = batches.Count, Items = batches[i]
            }, ct);
            MarkReported(batches[i]);
        }

        _checkTimer ??= new Timer(_ => _ = ReportChangedAsync(), null, LocalCheckInterval, LocalCheckInterval);
    }

    /// <summary>Delta report of the given accounts (all linked accounts when null).</summary>
    public async Task ReportAsync(IReadOnlyCollection<int>? accountIds, CancellationToken ct)
    {
        var connection = _connection();
        if (connection is not {IsConnected: true}) return;
        foreach (var chunk in _states(accountIds).Chunk(ServerAccountMirror.PAGE_SIZE))
        {
            await connection.InvokeAsync<AckResponse>("ReportAccountStates",
                new AccountStateReport {Full = false, Items = chunk}, ct);
            MarkReported(chunk);
        }
    }

    /// <summary>The same as <see cref="ReportAsync" />; failures are only logged (the next full report sends them).</summary>
    public async Task TryReportAsync(IReadOnlyCollection<int>? accountIds, CancellationToken ct)
    {
        try
        {
            await ReportAsync(accountIds, ct);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Account state report failed; sent with the next full report", ex);
        }
    }

    /// <summary>
    ///     The local state of accounts may have changed: after a short delay that coalesces bursts, the accounts whose state
    ///     changed are reported. Any thread; never blocks.
    /// </summary>
    public void NotifyLocalChange()
    {
        if (Interlocked.Exchange(ref _localChangeQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(LocalChangeDelay);
            Interlocked.Exchange(ref _localChangeQueued, 0);
            await ReportChangedAsync();
        });
    }

    /// <summary>Reports the accounts whose state or grants differ from what the server got last; the number sent.</summary>
    public async Task<int> ReportChangedAsync()
    {
        var connection = _connection();
        if (connection is not {IsConnected: true}) return 0;
        try
        {
            var items = _states(null);
            List<AccountStateItem> changed;
            lock (_reported) changed = items.Where(i => _reported.GetValueOrDefault(i.AccountId) != Signature(i)).ToList();
            if (changed.Count == 0) return 0;
            foreach (var chunk in changed.Chunk(ServerAccountMirror.PAGE_SIZE))
            {
                await connection.InvokeAsync<AckResponse>("ReportAccountStates",
                    new AccountStateReport {Full = false, Items = chunk}, CancellationToken.None);
                MarkReported(chunk);
            }

            NebulaLinkLog.Debug($"Local change: {changed.Count} account state(s) reported");
            return changed.Count;
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Account state report after a local change failed", ex);
            return 0;
        }
    }

    /// <summary>The link is gone: nothing was reported to a next link yet.</summary>
    public void Reset()
    {
        lock (_reported) _reported.Clear();
    }

    public void Stop()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    private void MarkReported(IEnumerable<AccountStateItem> items)
    {
        lock (_reported)
        {
            foreach (var item in items) _reported[item.AccountId] = Signature(item);
        }
    }

    private static string Signature(AccountStateItem item)
    {
        return item.State + "|" + string.Join(",", item.Grants.Select(g => g.Category + ":" + g.ModuleId));
    }
}
