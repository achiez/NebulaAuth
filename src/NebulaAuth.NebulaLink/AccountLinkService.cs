using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.NebulaLink.Sessions;
using SteamLib;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     The local accounts and their links to Achiefy accounts: matching, link / unlink, optional permissions, the choice
///     among duplicate mafiles, the state of each linked account and the failed link attempts of this session.
/// </summary>
internal sealed class AccountLinkService
{
    private readonly Action<MobileDataExtended>? _accountLinked;
    private readonly Func<NebulaModuleCatalog> _catalog;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConfirmationService _confirmations;
    private readonly Func<NebulaConnection?> _connection;
    private readonly Func<MobileDataExtended, MafileFileInfo?>? _fileInfo;
    private readonly HeadlessSessionPolicy _headless;
    private readonly Dictionary<int, string> _linkFailures = new();
    private readonly LinkStore _links;
    private readonly Func<IReadOnlyList<MobileDataExtended>> _mafiles;
    private readonly ServerAccountMirror _mirror;
    private readonly AccountSteamClientPool _pool;
    private readonly Action<NebulaLinkChange> _raise;

    public AccountLinkService(LinkStore links, ServerAccountMirror mirror, HeadlessSessionPolicy headless,
        ConfirmationService confirmations, AccountSteamClientPool pool, Func<IReadOnlyList<MobileDataExtended>> mafiles,
        Func<MobileDataExtended, MafileFileInfo?>? fileInfo, Func<NebulaModuleCatalog> catalog, Func<NebulaConnection?> connection,
        Action<MobileDataExtended>? accountLinked, Func<DateTimeOffset> clock, Action<NebulaLinkChange> raise)
    {
        _links = links;
        _mirror = mirror;
        _headless = headless;
        _confirmations = confirmations;
        _pool = pool;
        _mafiles = mafiles;
        _fileInfo = fileInfo;
        _catalog = catalog;
        _connection = connection;
        _accountLinked = accountLinked;
        _clock = clock;
        _raise = raise;
    }

    /// <summary>Set after construction (the reporter reads the states of this service).</summary>
    public AccountStateReporter Reporter { get; set; } = null!;

    #region Local accounts and states

    /// <summary>The local accounts with duplicate mafiles resolved (one mafile per login, see <see cref="LocalAccounts" />).</summary>
    public LocalAccounts ResolveLocal()
    {
        return LocalAccounts.Resolve(_mafiles(), _links.Snapshot()?.DuplicateChoices, _fileInfo);
    }

    /// <summary>Local matching of the mafiles with the loaded Achiefy accounts.</summary>
    public IReadOnlyList<MatchRow> MatchRows()
    {
        return AccountMatcher.Match(ResolveLocal(), _mirror.Accounts());
    }

    /// <summary>State of a linked account as reported to the server (<see cref="NebulaAccountStates" />).</summary>
    public string ComputeAccountState(NebulaAccountLink link, LocalAccounts index)
    {
        if (!index.ByLogin.TryGetValue(link.Login, out var mafile))
            return index.IsUnresolved(link.Login) ? NebulaAccountStates.MatchConflict : NebulaAccountStates.MafileMissing;
        return _headless.CheckReady(mafile);
    }

    /// <summary>State of every linked account by normalized login (one pass; account list icons).</summary>
    public IReadOnlyDictionary<string, string> AccountStates()
    {
        var index = ResolveLocal();
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in _links.Accounts()) states[link.Login] = ComputeAccountState(link, index);
        return states;
    }

    /// <summary>Linked accounts that need attention + failed account links of this session.</summary>
    public int ProblemsCount()
    {
        var count = AccountStates().Values.Count(s => s != NebulaAccountStates.Ready);
        lock (_linkFailures) count += _linkFailures.Count;
        return count;
    }

    /// <summary>The report items of the linked accounts (all, or of these account ids).</summary>
    public List<AccountStateItem> StateItems(IReadOnlyCollection<int>? accountIds)
    {
        var index = ResolveLocal();
        return _links.Accounts().Where(l => accountIds == null || accountIds.Contains(l.AccountId))
            .Select(l => new AccountStateItem
            {
                AccountId = l.AccountId,
                State = ComputeAccountState(l, index),
                Grants = l.Grants.Take(NebulaCatalogLimits.MaxModules).Select(g => g.ToWire()).ToArray()
            }).ToList();
    }

    /// <summary>Last link / unlink failure of an account in this session (protocol result string).</summary>
    public string? LinkFailure(int accountId)
    {
        lock (_linkFailures) return _linkFailures.GetValueOrDefault(accountId);
    }

    public void ClearFailures()
    {
        lock (_linkFailures) _linkFailures.Clear();
    }

    internal void SetLinkFailure(int accountId, string result)
    {
        lock (_linkFailures) _linkFailures[accountId] = result;
    }

    #endregion

    #region Changes

    /// <summary>Links (or unlinks) the given rows; grants are the optional scopes turned on for new links.</summary>
    public async Task<BulkOutcome> SetAccountLinksAsync(IReadOnlyList<MatchRow> rows, bool link,
        IReadOnlyCollection<GrantKey> grants, CancellationToken ct)
    {
        var outcome = new BulkOutcome();
        var connection = _connection();
        if (connection is not {IsConnected: true})
        {
            outcome.Error = NebulaResultCodes.DeviceOffline;
            outcome.Skipped = rows.Count;
            return outcome;
        }

        var catalog = _catalog();
        var validGrants = grants.Where(g => ModuleCatalogs.Contains(catalog, g)).Distinct().ToList();
        var changes = new List<(AccountLinkChange Change, MatchRow Row)>();
        foreach (var row in rows)
        {
            var existing = row.Server != null ? _links.FindByAccountId(row.Server.AccountId) : null;
            if (!link && existing != null)
            {
                // Unlink works without the mafile too (removed / renamed locally)
                changes.Add((new AccountLinkChange
                {
                    AccountId = existing.AccountId, Action = NebulaAccountLinkActions.Unlink, Login = existing.Login
                }, row));
                continue;
            }

            if (row.Server == null || row.Local == null)
            {
                outcome.Skipped++;
                if (row.Server != null) outcome.Reasons[row.Server.AccountId] = row.Kind.ToString();
                continue;
            }

            var linked = existing != null;
            if (link && (!row.IsLinkable || linked))
            {
                outcome.Skipped++;
                outcome.Reasons[row.Server.AccountId] = linked ? NebulaAccountLinkResults.AlreadyLinked : row.Kind.ToString();
                continue;
            }

            if (!link && !linked)
            {
                outcome.Skipped++;
                outcome.Reasons[row.Server.AccountId] = NebulaAccountLinkResults.NotLinked;
                continue;
            }

            changes.Add((new AccountLinkChange
            {
                AccountId = row.Server.AccountId,
                Action = link ? NebulaAccountLinkActions.Link : NebulaAccountLinkActions.Unlink,
                Login = row.Login
            }, row));
        }

        foreach (var chunk in changes.Chunk(ServerAccountMirror.PAGE_SIZE))
        {
            SetAccountLinksResponse response;
            try
            {
                response = await connection.InvokeAsync<SetAccountLinksResponse>("SetAccountLinks",
                    new SetAccountLinksRequest {Items = chunk.Select(c => c.Change).ToArray()}, ct);
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("SetAccountLinks failed", ex);
                outcome.Error = NebulaConnection.HubErrorCode(ex);
                outcome.Skipped += chunk.Length;
                foreach (var c in chunk) outcome.Reasons[c.Change.AccountId] = NebulaAccountLinkResults.Failed;
                continue;
            }

            var linkedRows = ApplyResults(chunk, response, link, outcome);
            if (linkedRows.Count > 0)
            {
                ApplyLocally(linkedRows, [], validGrants);
                // The links are kept; a failed report is sent again with the next full report
                await Reporter.TryReportAsync(linkedRows.Select(r => r.Server!.AccountId).ToList(), ct);
            }
        }

        NebulaLinkLog.Info($"Account {(link ? "link" : "unlink")}: changed {outcome.Changed}, skipped {outcome.Skipped}");
        _raise(NebulaLinkChange.Accounts);
        return outcome;
    }

    /// <summary>The server's answers: failures recorded, unlinks applied; the rows to link are returned.</summary>
    private List<MatchRow> ApplyResults((AccountLinkChange Change, MatchRow Row)[] chunk, SetAccountLinksResponse response,
        bool link, BulkOutcome outcome)
    {
        var byId = chunk.ToDictionary(c => c.Change.AccountId);
        var linkedRows = new List<MatchRow>();
        var unlinked = new List<AccountLinkChange>();
        foreach (var item in response.Items)
        {
            if (!byId.TryGetValue(item.AccountId, out var entry)) continue;
            var ok = link
                ? item.Result is NebulaAccountLinkResults.Linked or NebulaAccountLinkResults.AlreadyLinked
                : item.Result is NebulaAccountLinkResults.Unlinked or NebulaAccountLinkResults.NotLinked
                    or NebulaAccountLinkResults.NotFound;
            lock (_linkFailures)
            {
                if (ok) _linkFailures.Remove(item.AccountId);
                else _linkFailures[item.AccountId] = item.Result;
            }

            if (!ok)
            {
                outcome.Skipped++;
                outcome.Reasons[item.AccountId] = item.Result;
                continue;
            }

            outcome.Changed++;
            if (item.Source != null) _mirror.SetSource(item.AccountId, item.Source);
            if (link) linkedRows.Add(entry.Row);
            else unlinked.Add(entry.Change);
        }

        if (unlinked.Count > 0) ApplyLocally([], unlinked, []);
        return linkedRows;
    }

    private void ApplyLocally(List<MatchRow> linkedRows, List<AccountLinkChange> unlinked, List<GrantKey> grants)
    {
        var now = _clock();
        var unlinkedIds = unlinked.Select(u => u.AccountId).ToHashSet();
        _links.Update(d =>
        {
            foreach (var row in linkedRows)
            {
                d.Accounts.RemoveAll(a => a.Login == row.Login || a.AccountId == row.Server!.AccountId);
                d.Accounts.Add(new NebulaAccountLink
                {
                    Login = row.Login, AccountId = row.Server!.AccountId, Grants = grants.ToList(), LinkedAt = now
                });
            }

            d.Accounts.RemoveAll(a => unlinkedIds.Contains(a.AccountId));
        });
        foreach (var row in linkedRows) OnAccountLinked(row.Local!);
        foreach (var change in unlinked) Forget(change.Login);
    }

    /// <summary>The in-memory confirmation list and Steam client of an account that is not linked any more.</summary>
    public void Forget(string login)
    {
        _confirmations.Invalidate(login);
        _pool.Remove(login);
    }

    /// <summary>A linked account is not auto-confirmed any more (the app forgets its saved auto-confirmation flags).</summary>
    private void OnAccountLinked(MobileDataExtended mafile)
    {
        try
        {
            _accountLinked?.Invoke(mafile);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Auto-confirmation could not be turned off for a linked account", ex);
        }
    }

    /// <summary>Turns an optional grant on/off for linked accounts (saved at once, reported to the server).</summary>
    public async Task<BulkOutcome> SetGrantAsync(IReadOnlyCollection<string> logins, GrantKey scope, bool on, CancellationToken ct)
    {
        var outcome = new BulkOutcome();
        if (on && !ModuleCatalogs.Contains(_catalog(), scope))
        {
            outcome.Error = NebulaResultCodes.Unsupported;
            outcome.Skipped = logins.Count;
            return outcome;
        }

        var changedIds = new List<int>();
        _links.Update(d =>
        {
            foreach (var account in d.Accounts.Where(a => logins.Contains(a.Login)))
            {
                if (account.Grants.Contains(scope) == on)
                {
                    outcome.Skipped++;
                    continue;
                }

                if (on) account.Grants.Add(scope);
                else account.Grants.Remove(scope);
                changedIds.Add(account.AccountId);
                outcome.Changed++;
            }
        });
        outcome.Skipped += logins.Count - outcome.Changed - outcome.Skipped;
        if (changedIds.Count > 0)
        {
            NebulaLinkLog.Info($"Grant {scope} {(on ? "on" : "off")} for {changedIds.Count} account(s)");
            await Reporter.TryReportAsync(changedIds, ct);
        }

        _raise(NebulaLinkChange.Accounts);
        return outcome;
    }

    /// <summary>
    ///     "Use this one" for a login with several local mafiles: the choice (file name and the files offered now) goes to
    ///     nebula.json; the account is reported again.
    /// </summary>
    public async Task ChooseDuplicateAsync(string login, string fileName, CancellationToken ct)
    {
        var normalized = AccountMatcher.Normalize(login);
        var group = ResolveLocal().GroupOf(normalized) ?? throw new InvalidOperationException("No duplicate mafiles");
        if (group.Candidates.All(c => !string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Unknown file");
        _links.Update(d =>
        {
            d.DuplicateChoices.RemoveAll(c => c.Login == normalized);
            d.DuplicateChoices.Add(new DuplicateChoice
            {
                Login = normalized, FileName = fileName,
                Candidates = group.Candidates.Select(c => c.FileName).OfType<string>().ToList()
            });
        });
        NebulaLinkLog.Info($"Duplicate mafiles of an account resolved by the user ({group.Candidates.Count} files)");
        _raise(NebulaLinkChange.Accounts);
        await Reporter.TryReportAsync(null, ct);
    }

    #endregion
}
