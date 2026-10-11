using System;
using System.Collections.Generic;
using System.Linq;
using SteamLib;

namespace NebulaAuth.NebulaLink;

/// <summary>File name and modification time of a local mafile (supplied by the app; null when unknown).</summary>
public sealed record MafileFileInfo(string? FileName, DateTimeOffset? Modified);

/// <summary>One of several local mafiles with the same login.</summary>
public sealed record MafileCandidate(
    MobileDataExtended Mafile,
    string? FileName,
    DateTimeOffset? Modified,
    string? SteamId,
    bool HasSession);

/// <summary>Local mafiles that share a login, and the file used for it (null while the user has to choose).</summary>
public sealed class DuplicateGroup
{
    public required string Login { get; init; }
    public required IReadOnlyList<MafileCandidate> Candidates { get; init; }
    public MafileCandidate? Chosen { get; init; }
    public bool IsResolved => Chosen != null;
}

/// <summary>
///     The local accounts as Nebula Link sees them: one mafile per login. Duplicates of a login are resolved (§ duplicate
///     mafiles): the same shared secret in every file → the newest file; otherwise the user's choice stored in
///     nebula.json, which stops counting when the chosen file disappears or a new file with another secret appears. An
///     unresolved login has no mafile here (its account is not connected, requests answer match_conflict). Files are
///     never renamed or deleted.
/// </summary>
public sealed class LocalAccounts
{
    private readonly Dictionary<string, DuplicateGroup> _groups;

    private LocalAccounts(List<MobileDataExtended> effective, Dictionary<string, DuplicateGroup> groups)
    {
        Effective = effective;
        _groups = groups;
        foreach (var mafile in effective) ByLogin.TryAdd(AccountMatcher.Normalize(mafile.AccountName), mafile);
    }

    /// <summary>One mafile per login; logins with unresolved duplicates are left out.</summary>
    public IReadOnlyList<MobileDataExtended> Effective { get; }

    /// <summary>The mafile of each login of <see cref="Effective" /> (normalized login → mafile).</summary>
    public Dictionary<string, MobileDataExtended> ByLogin { get; } = new(StringComparer.Ordinal);

    /// <summary>Logins with several local mafiles (normalized login → group).</summary>
    public IReadOnlyDictionary<string, DuplicateGroup> Duplicates => _groups;

    public IEnumerable<DuplicateGroup> Unresolved => _groups.Values.Where(g => !g.IsResolved);

    /// <summary>The login has several local mafiles and none is chosen (its account must not be served).</summary>
    public bool IsUnresolved(string login)
    {
        return _groups.TryGetValue(login, out var group) && !group.IsResolved;
    }

    public DuplicateGroup? GroupOf(string? login)
    {
        return login != null && _groups.TryGetValue(AccountMatcher.Normalize(login), out var g) ? g : null;
    }

    /// <summary>Without choices (tests, previews): only the same-secret rule applies.</summary>
    public static LocalAccounts Of(IReadOnlyList<MobileDataExtended> mafiles)
    {
        return Resolve(mafiles, null, null);
    }

    public static LocalAccounts Resolve(IReadOnlyList<MobileDataExtended> mafiles,
        IReadOnlyList<DuplicateChoice>? choices, Func<MobileDataExtended, MafileFileInfo?>? fileInfo)
    {
        var effective = new List<MobileDataExtended>(mafiles.Count);
        var groups = new Dictionary<string, DuplicateGroup>(StringComparer.Ordinal);
        foreach (var byLogin in mafiles.GroupBy(m => AccountMatcher.Normalize(m.AccountName)))
        {
            var files = byLogin.ToList();
            if (files.Count == 1)
            {
                effective.Add(files[0]);
                continue;
            }

            var candidates = files.Select(m =>
            {
                MafileFileInfo? info = null;
                try
                {
                    info = fileInfo?.Invoke(m);
                }
                catch
                {
                    // Unknown file data: the candidate is still listed
                }

                return new MafileCandidate(m, info?.FileName, info?.Modified, LocalSteamIdOf(m),
                    m.SessionData?.RefreshToken is {IsExpired: false});
            }).ToList();

            var chosen = Choose(byLogin.Key, candidates, choices);
            groups[byLogin.Key] = new DuplicateGroup {Login = byLogin.Key, Candidates = candidates, Chosen = chosen};
            if (chosen != null) effective.Add(chosen.Mafile);
        }

        return new LocalAccounts(effective, groups);
    }

    private static MafileCandidate? Choose(string login, List<MafileCandidate> candidates,
        IReadOnlyList<DuplicateChoice>? choices)
    {
        // 1. One authenticator in several files
        var secret = candidates[0].Mafile.SharedSecret;
        if (candidates.All(c => string.Equals(c.Mafile.SharedSecret, secret, StringComparison.Ordinal)))
            return Newest(candidates);

        // 2. The user's choice, while the file exists and no new file with another secret appeared
        var choice = choices?.FirstOrDefault(c => c.Login == login);
        if (choice == null) return null;
        var picked = candidates.FirstOrDefault(c => string.Equals(c.FileName, choice.FileName, StringComparison.OrdinalIgnoreCase));
        if (picked == null) return null;
        var newWithOtherSecret = candidates.Any(c =>
            !choice.Candidates.Contains(c.FileName ?? "", StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(c.Mafile.SharedSecret, picked.Mafile.SharedSecret, StringComparison.Ordinal));
        return newWithOtherSecret ? null : picked;
    }

    /// <summary>Shown next to a duplicate file so the user can tell the files apart; never compared with Achiefy.</summary>
    private static string? LocalSteamIdOf(MobileDataExtended mafile)
    {
        var id = (mafile.SessionData?.SteamId ?? mafile.SteamId).Steam64.ToUlong();
        return id == 0 ? null : id.ToString();
    }

    private static MafileCandidate Newest(List<MafileCandidate> candidates)
    {
        return candidates.OrderByDescending(c => c.Modified ?? DateTimeOffset.MinValue).First();
    }
}
