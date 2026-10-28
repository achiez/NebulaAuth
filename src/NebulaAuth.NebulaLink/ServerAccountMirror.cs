using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Protocol;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     The Achiefy accounts of the tenant as the server last reported them. Syncs run one at a time, so a full sync and a
///     partial one (AccountsChanged) never overwrite each other. Local links of accounts that no longer exist are dropped.
/// </summary>
internal sealed partial class ServerAccountMirror(LinkStore links)
{
    public const int PAGE_SIZE = 500;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private Dictionary<int, AchiefyAccount> _accounts = new();

    /// <summary>The accounts have been loaded from the server since the link was (re)established.</summary>
    public bool Loaded { get; private set; }

    public IReadOnlyList<AchiefyAccount> Accounts()
    {
        lock (_lock) return _accounts.Values.ToList();
    }

    public AchiefyAccount? Account(int accountId)
    {
        lock (_lock) return _accounts.GetValueOrDefault(accountId);
    }

    /// <summary>The source of an account changed by a link / unlink (the server's answer).</summary>
    public void SetSource(int accountId, string source)
    {
        lock (_lock)
        {
            if (_accounts.TryGetValue(accountId, out var account)) account.Source = source;
        }
    }

    public void Clear()
    {
        lock (_lock) _accounts = new Dictionary<int, AchiefyAccount>();
        Loaded = false;
    }

    /// <summary>Loads the Achiefy accounts (all pages, or the given ids).</summary>
    public async Task SyncAsync(NebulaConnection connection, IReadOnlyCollection<int>? accountIds, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            if (accountIds == null) await SyncAllAsync(connection, ct);
            else await SyncSomeAsync(connection, accountIds, ct);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task SyncAllAsync(NebulaConnection connection, CancellationToken ct)
    {
        var all = new Dictionary<int, AchiefyAccount>();
        string? cursor = null;
        do
        {
            var page = await connection.InvokeAsync<AccountsPage>("SyncAccounts",
                new AccountsQuery {Cursor = cursor, PageSize = PAGE_SIZE}, ct);
            foreach (var account in page.Items) all[account.AccountId] = Normalize(account);
            cursor = page.NextCursor;
        } while (cursor != null);

        lock (_lock) _accounts = all;
        Loaded = true;
        // Local links of accounts that no longer exist in Achiefy are dropped (their grants too)
        var gone = links.Accounts().Where(l => !all.ContainsKey(l.AccountId)).Select(l => l.AccountId).ToHashSet();
        if (gone.Count > 0)
        {
            links.Update(d => d.Accounts.RemoveAll(a => gone.Contains(a.AccountId)));
            NebulaLinkLog.Info($"{gone.Count} local account link(s) removed: the accounts no longer exist in Achiefy");
        }
    }

    private async Task SyncSomeAsync(NebulaConnection connection, IReadOnlyCollection<int> accountIds, CancellationToken ct)
    {
        foreach (var chunk in accountIds.Distinct().Chunk(PAGE_SIZE))
        {
            var page = await connection.InvokeAsync<AccountsPage>("SyncAccounts",
                new AccountsQuery {PageSize = PAGE_SIZE, AccountIds = chunk}, ct);
            lock (_lock)
            {
                var copy = new Dictionary<int, AchiefyAccount>(_accounts);
                foreach (var account in page.Items) copy[account.AccountId] = Normalize(account);
                foreach (var id in page.DeletedAccountIds) copy.Remove(id);
                _accounts = copy;
            }

            if (page.DeletedAccountIds.Length > 0)
                links.Update(d => d.Accounts.RemoveAll(a => page.DeletedAccountIds.Contains(a.AccountId)));
        }
    }

    private static AchiefyAccount Normalize(AchiefyAccount a)
    {
        a.Login = AccountMatcher.Normalize(a.Login);
        return a;
    }
}
