using System;
using System.Collections.Generic;
using System.Linq;
using NebulaAuth.NebulaLink.Protocol;
using SteamLib;

namespace NebulaAuth.NebulaLink;

public enum MatchKind
{
    /// <summary>The same login locally (one mafile) and in Achiefy: can be linked.</summary>
    Match,

    /// <summary>Several local mafiles have this login and none could be picked automatically: the user chooses.</summary>
    DuplicateMafile,

    OnlyLocal,
    OnlyAchiefy
}

/// <summary>
///     One row of the accounts table: a local mafile and/or an Achiefy account. <see cref="Duplicates" />: the local
///     files of a <see cref="MatchKind.DuplicateMafile" /> row.
/// </summary>
public sealed record MatchRow(
    string Login,
    MobileDataExtended? Local,
    AchiefyAccount? Server,
    MatchKind Kind,
    DuplicateGroup? Duplicates = null)
{
    public bool IsLinkable => Kind == MatchKind.Match && Local != null && Server != null;
}

/// <summary>
///     Local matching of mafiles with the Achiefy accounts of the tenant by normalized login (Trim + lower case; a login
///     is unique in a tenant). The SteamID of a mafile is not used. Pure function; nothing is sent anywhere.
/// </summary>
public static class AccountMatcher
{
    public static string Normalize(string? login)
    {
        return (login ?? "").Trim().ToLowerInvariant();
    }

    public static IReadOnlyList<MatchRow> Match(IReadOnlyList<MobileDataExtended> local, IReadOnlyList<AchiefyAccount> server)
    {
        return Match(LocalAccounts.Of(local), server);
    }

    public static IReadOnlyList<MatchRow> Match(LocalAccounts local, IReadOnlyList<AchiefyAccount> server)
    {
        var serverByLogin = new Dictionary<string, AchiefyAccount>(StringComparer.Ordinal);
        foreach (var account in server) serverByLogin.TryAdd(Normalize(account.Login), account);
        var rows = new List<MatchRow>(Math.Max(local.Effective.Count, server.Count));
        var usedLogins = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mafile in local.Effective)
        {
            var login = Normalize(mafile.AccountName);
            if (serverByLogin.TryGetValue(login, out var account))
            {
                usedLogins.Add(login);
                rows.Add(new MatchRow(login, mafile, account, MatchKind.Match));
            }
            else
            {
                rows.Add(new MatchRow(login, mafile, null, MatchKind.OnlyLocal));
            }
        }

        // Logins whose local duplicates the user has to resolve: one row with the candidates
        foreach (var group in local.Unresolved)
        {
            serverByLogin.TryGetValue(group.Login, out var account);
            usedLogins.Add(group.Login);
            rows.Add(new MatchRow(group.Login, null, account, MatchKind.DuplicateMafile, group));
        }

        foreach (var account in server)
        {
            var login = Normalize(account.Login);
            if (usedLogins.Add(login)) rows.Add(new MatchRow(login, null, account, MatchKind.OnlyAchiefy));
        }

        return rows;
    }
}
