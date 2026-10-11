using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AchiesUtilities.Models;
using NebulaAuth.Model.Entities;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Protocol;
using SteamLibForked.Models.Core;
using SteamLibForked.Models.Session;

namespace NebulaAuth.ViewModel.NebulaLink.Demo;

/// <summary>
///     An in-memory Nebula Link with generated accounts and request history for the modal previews
///     and the main window demos (no files, no network). The rows cover every match kind, account state and error.
/// </summary>
internal static class FakeNebulaLink
{
    private const long BASE_STEAM_ID = 76561198100000000;

    private static readonly string[] Syllables =
    [
        "al", "ex", "ra", "mo", "ni", "ko", "ze", "ta", "vi", "lu", "dar", "ion", "rex", "sol", "kai", "nox", "fen", "gor",
        "bel", "tur"
    ];

    /// <summary>The published origins as the previews see them (null: the list could not be fetched).</summary>
    public static void SetOrigins(bool available)
    {
        NebulaLinkApis.SetFetched(available ? ["https://example.com/api"] : null);
    }

    private static readonly GrantKey Harvester = new(NebulaCategories.Trade, NebulaModules.Harvester);
    private static readonly GrantKey P2PSeller = new(NebulaCategories.Trade, NebulaModules.P2PSeller);
    private static readonly GrantKey SteamSeller = new(NebulaCategories.MarketSell, NebulaModules.SteamSeller);

    /// <summary>File name and a fixed modification time of the generated mafiles (duplicate candidates).</summary>
    private static MafileFileInfo? FileInfo(SteamLib.MobileDataExtended m)
    {
        if (m is not Mafile mafile) return null;
        var seed = mafile.Filename?.Length ?? 0;
        return new MafileFileInfo(mafile.Filename, new DateTimeOffset(2026, 9, 1 + seed % 27, 10 + seed % 9, 15, 0, TimeSpan.Zero));
    }

    public static NebulaLinkIntegration Create(NebulaLinkState state, int count = 2000, bool storeAvailable = true)
    {
        var (mafiles, server, links, invalidPassword) = Build(count);
        var integration = NebulaLinkIntegration.CreatePreview(state, mafiles, server, links, storeAvailable,
            fileInfo: FileInfo, invalidPasswordLogins: invalidPassword);
        integration.PreviewSetLinkFailure(server[7].AccountId, NebulaAccountLinkResults.LoginMismatch);
        SeedHistory(integration, links);
        return integration;
    }

    /// <summary>
    ///     Same data around the loaded accounts (main window demo): accounts 1-4 connected; with
    ///     <paramref name="problems" /> account 3 has a rejected saved password, account 4 needs a Steam login and the
    ///     connect attempt of account 6 failed.
    /// </summary>
    public static NebulaLinkIntegration CreateFor(IReadOnlyList<Mafile> mafiles, NebulaLinkState state, bool problems)
    {
        var server = new List<AchiefyAccount>();
        var links = new List<NebulaAccountLink>();
        var invalidPassword = new List<string>();
        for (var i = 0; i < mafiles.Count; i++)
        {
            var mafile = mafiles[i];
            var login = AccountMatcher.Normalize(mafile.AccountName);
            if (i < 4)
            {
                // In-memory sessions (never saved): ready unless this demo shows a missing Steam login
                mafile.SessionData = problems && i == 3 ? null : Session(mafile.SteamId.Steam64.ToLong(), true);
            }

            if (i % 5 == 4) continue; // only in NebulaAuth
            var account = new AchiefyAccount
            {
                AccountId = 100 + i, Login = login,
                Source = i < 4 ? NebulaAuthenticatorSources.Nebula : NebulaAuthenticatorSources.CloudMafile,
                HasServerMafile = true, LinkedToThisDevice = i < 4
            };
            server.Add(account);
            if (i < 4)
            {
                links.Add(new NebulaAccountLink
                {
                    AccountId = account.AccountId, Login = login, LinkedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    Grants = i == 0 ? [Harvester, SteamSeller] : []
                });
                if (problems && i == 2) invalidPassword.Add(login);
            }
        }

        var integration = NebulaLinkIntegration.CreatePreview(state, mafiles, server, links, fileInfo: FileInfo,
            invalidPasswordLogins: invalidPassword);
        if (problems && server.Count > 5) integration.PreviewSetLinkFailure(server[5].AccountId, NebulaAccountLinkResults.LoginMismatch);
        SeedHistory(integration, links);
        return integration;
    }

    private static (List<Mafile>, List<AchiefyAccount>, List<NebulaAccountLink>, List<string>) Build(int count)
    {
        var random = new Random(42);
        var mafiles = new List<Mafile>(count);
        var server = new List<AchiefyAccount>(count);
        var links = new List<NebulaAccountLink>();
        var invalidPassword = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var login = Login(random, i);
            var steamId = BASE_STEAM_ID + i;
            var kind = i switch
            {
                < 6 => "linked",
                7 => "loginmismatch",
                8 or 9 => "duplicate",
                10 or 16 => "onlyachiefy",
                11 => "invalidpw",
                12 => "session",
                15 => "onlylocal",
                17 or 18 => "samesecret",
                _ => (i % 10) switch
                {
                    0 or 1 or 2 => "linked",
                    3 => "onlylocal",
                    5 => "onlyachiefy",
                    _ => "match"
                }
            };
            // Two files of one login with different secrets: the user chooses
            if (kind == "duplicate") login = "trader_dup";
            // Two files of one authenticator: the newest is used silently
            if (kind == "samesecret") login = "aalto_backup";
            var hasLocal = kind != "onlyachiefy";
            var hasServer = kind is not "onlylocal" && !(kind is "duplicate" or "samesecret" && i is 9 or 18);
            var linked = kind is "linked" or "invalidpw" or "session";
            if (hasLocal)
            {
                var mafile = new Mafile
                {
                    AccountName = login,
                    SharedSecret = kind == "duplicate" && i == 9 ? "Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MD0=" : "lxHd6IM+av7V+Zf7f6V3ZQ03Hh0=",
                    Filename = kind is "duplicate" or "samesecret"
                        ? login + (i % 2 == 0 ? ".maFile" : " (copy).maFile")
                        : steamId.ToString(CultureInfo.InvariantCulture) + ".maFile",
                    IdentitySecret = "s6lVIKcj3+k7NmkDiO6NiSiUkL8=", DeviceId = "android:preview",
                    SteamId = SteamId.FromSteam64(steamId)
                };
                if (kind != "session") mafile.SessionData = Session(steamId, true);
                mafiles.Add(mafile);
            }

            if (!hasServer) continue;
            var account = new AchiefyAccount
            {
                AccountId = 1000 + i,
                Login = login,
                Source = linked ? NebulaAuthenticatorSources.Nebula
                    : i % 4 == 0 ? NebulaAuthenticatorSources.None : NebulaAuthenticatorSources.CloudMafile,
                HasServerMafile = i % 3 != 0,
                LinkedToThisDevice = linked
            };
            server.Add(account);
            if (linked)
            {
                links.Add(new NebulaAccountLink
                {
                    AccountId = account.AccountId,
                    Login = login,
                    LinkedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    Grants = i % 3 == 0
                        ? [Harvester, SteamSeller]
                        : i % 3 == 1 ? [P2PSeller] : []
                });
                if (kind == "invalidpw") invalidPassword.Add(login);
            }
        }

        // A link whose mafile was removed locally
        links.Add(new NebulaAccountLink
        {
            AccountId = 999, Login = "removed_local",
            LinkedAt = DateTimeOffset.UtcNow.AddDays(-5)
        });
        server.Add(new AchiefyAccount
        {
            AccountId = 999, Login = "removed_local",
            Source = NebulaAuthenticatorSources.Nebula, LinkedToThisDevice = true
        });
        return (mafiles, server, links, invalidPassword);
    }

    private static string Login(Random random, int i)
    {
        var parts = random.Next(2, 4);
        var s = string.Concat(Enumerable.Range(0, parts).Select(_ => Syllables[random.Next(Syllables.Length)]));
        return s + (i % 4 == 0
            ? random.Next(10, 999).ToString(CultureInfo.InvariantCulture)
            : "_" + i.ToString(CultureInfo.InvariantCulture));
    }

    public static MobileSessionData Session(long steamId, bool valid)
    {
        var expires = UnixTimeStamp.FromDateTime(DateTime.UtcNow.AddDays(valid ? 100 : -1));
        var refresh = new SteamAuthToken("preview-refresh", steamId, expires, SteamDomain.Community,
            SteamAccessTokenType.MobileRefresh);
        var mobile = new SteamAuthToken("preview-mobile", steamId, expires, SteamDomain.Community, SteamAccessTokenType.Mobile);
        return new MobileSessionData("preview", SteamId.FromSteam64(steamId), refresh, mobile,
            (IEnumerable<SteamAuthToken>?) null);
    }

    /// <summary>Request history with every kind of result (done, waiting, confirmed, refused, errors) and manual decisions.</summary>
    private static void SeedHistory(NebulaLinkIntegration integration, List<NebulaAccountLink> links)
    {
        if (links.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            (NebulaCommandTypes.LoginCode, NebulaModules.AccountRuntime, (string?) null, (string?) null, NebulaResultCodes.Ok),
            (NebulaCommandTypes.CheckConfirmation, NebulaModules.Harvester, NebulaOperationTypes.Trade, null, NebulaResultCodes.Pending),
            (NebulaCommandTypes.Confirm, NebulaModules.Harvester, NebulaOperationTypes.Trade, NebulaDecisions.Accept, NebulaResultCodes.Confirmed),
            (NebulaCommandTypes.Confirm, NebulaModules.SteamSeller, NebulaOperationTypes.MarketListing, NebulaDecisions.Accept, NebulaResultCodes.Confirmed),
            (NebulaCommandTypes.Confirm, NebulaModules.P2PSeller, NebulaOperationTypes.Trade, NebulaDecisions.Accept, NebulaResultCodes.ScopeDenied),
            (NebulaCommandTypes.CheckReady, NebulaModules.AccountRuntime, null, null, NebulaResultCodes.Ok),
            (NebulaCommandTypes.CheckConfirmation, NebulaModules.Trader, NebulaOperationTypes.Trade, null, NebulaResultCodes.NotFound),
            (NebulaCommandTypes.Confirm, NebulaModules.SteamBuyer, NebulaOperationTypes.MarketPurchase, NebulaDecisions.Accept, NebulaResultCodes.InvalidLocalPassword),
            (NebulaCommandTypes.LoginCode, NebulaModules.AccountRuntime, null, null, NebulaResultCodes.SteamSessionRequired),
            (NebulaCommandTypes.Confirm, NebulaModules.Harvester, NebulaOperationTypes.Trade, NebulaDecisions.Decline, NebulaResultCodes.Declined),
            (NebulaCommandTypes.CheckConfirmation, NebulaModules.SteamSeller, NebulaOperationTypes.MarketListing, null, NebulaResultCodes.ProxyFailed),
            (NebulaCommandTypes.Confirm, NebulaModules.Trader, NebulaOperationTypes.Trade, NebulaDecisions.Accept, NebulaResultCodes.ResultUnknown)
        };
        var waiting = new List<RequestRecord>();
        for (var i = 0; i < 60; i++)
        {
            var link = links[i % Math.Min(links.Count, 8)];
            var (type, module, op, decision, result) = samples[i % samples.Length];
            var count = op == NebulaOperationTypes.MarketListing ? 3 : op != null ? 1 : 0;
            var record = new RequestRecord
            {
                RequestId = Guid.NewGuid(), Time = now.AddMinutes(-i * 7), AccountId = link.AccountId, Login = link.Login,
                Module = module, Type = type, OperationType = op, IdCount = count,
                Ids = Enumerable.Range(0, count).Select(n => (7458859849L + i * 10 + n).ToString(CultureInfo.InvariantCulture)).ToArray(),
                Decision = decision, Result = result
            };
            integration.History.AddRequest(record);
            if (result == NebulaResultCodes.Pending) waiting.Add(record);
        }

        // The user decided some of the operations Achiefy was waiting for: shown in their request rows
        for (var i = 0; i < Math.Min(2, waiting.Count); i++)
        {
            integration.History.AddDecision(new DecisionRecord
            {
                Login = waiting[i].Login!, Type = 2, CreatorId = waiting[i].Ids![0],
                Decision = i == 1 ? NebulaDecisions.Decline : NebulaDecisions.Accept, Origin = DecisionRecord.ORIGIN_MANUAL,
                Time = waiting[i].Time.AddMinutes(2)
            });
        }
    }
}
