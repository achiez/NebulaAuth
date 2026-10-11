using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Credentials;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.NebulaLink.Sessions;
using Newtonsoft.Json;
using SteamLib;
using SteamLib.SteamMobile.Confirmations;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Hooks for tests without UI input:
///     <list type="bullet">
///         <item>
///             NEBULA_LINK_SCRIPT: steps run once after startup, separated by ';': consent, pair=&lt;code&gt;,
///             link=exact (link every linkable row), grant=&lt;category&gt;:&lt;moduleId&gt; (turn a permission on for every linked account),
///             pause, resume, unlink, wait=&lt;ms&gt;.
///         </item>
///         <item>NEBULA_LINK_STATE_FILE: a JSON summary of the integration is written there on every change.</item>
///     </list>
///     Preview factory for the modal screenshots (<see cref="CreatePreview" />).
/// </summary>
public sealed partial class NebulaLinkIntegration
{
    private Timer? _debugStateTimer;
    private readonly List<string> _debugScriptLog = [];

    partial void DebugOnInitialized()
    {
        var stateFile = Environment.GetEnvironmentVariable("NEBULA_LINK_STATE_FILE");
        if (!string.IsNullOrWhiteSpace(stateFile))
        {
            _debugStateTimer = new Timer(_ => WriteDebugState(stateFile), null, 0, 500);
        }

        var script = Environment.GetEnvironmentVariable("NEBULA_LINK_SCRIPT");
        if (string.IsNullOrWhiteSpace(script)) return;
        _ = Task.Run(() => RunDebugScriptAsync(script));
    }

    private async Task RunDebugScriptAsync(string script)
    {
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (name, arg) = raw.IndexOf('=') is var i and > 0 ? (raw[..i], raw[(i + 1)..]) : (raw, "");
            string result;
            try
            {
                result = await RunDebugStepAsync(name, arg);
            }
            catch (Exception ex)
            {
                result = "error " + ex.GetType().Name + ": " + ex.Message;
            }

            lock (_debugScriptLog) _debugScriptLog.Add($"{name}: {result}");
            NebulaLinkLog.Info($"Debug script step {name}: {result}");
        }
    }

    private async Task<string> RunDebugStepAsync(string name, string arg)
    {
        switch (name)
        {
            case "consent":
                Enable();
                return "ok";
            case "pair":
                await RedeemAsync(arg, CancellationToken.None);
                return (await WaitForApprovalAsync(CancellationToken.None)).ToString();
            case "link":
            {
                await WaitFor(() => ServerAccountsLoaded && State == NebulaLinkState.Connected, 30000);
                var rows = MatchRows().Where(r => r.IsLinkable).ToList();
                var outcome = await SetAccountLinksAsync(rows, true, [], CancellationToken.None);
                return $"changed {outcome.Changed}, skipped {outcome.Skipped}";
            }
            case "grant":
            {
                // grant=<category>:<moduleId>, e.g. grant=market_sell:steam_seller
                var parts = arg.Split(':', 2);
                if (parts.Length != 2) return "expected <category>:<moduleId>";
                var ids = Links.Accounts().Select(a => a.Login).ToList();
                var outcome = await SetGrantAsync(ids, new GrantKey(parts[0], parts[1]), true, CancellationToken.None);
                return $"changed {outcome.Changed}";
            }
            case "pause":
                await PauseAsync();
                return "ok";
            case "resume":
                await ResumeAsync();
                return "ok";
            case "unlink":
                return (await UnlinkAsync()).ToString();
            case "wait":
                await Task.Delay(int.Parse(arg));
                return "ok";
            default:
                return "unknown step";
        }
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
    }

    private void WriteDebugState(string path)
    {
        try
        {
            var data = Links.Snapshot();
            List<string> log;
            lock (_debugScriptLog) log = _debugScriptLog.ToList();
            var json = JsonConvert.SerializeObject(new
            {
                state = State.ToString(),
                reason = StateReason,
                consent = ConsentGiven,
                paused = data?.Paused,
                linkId = data?.LinkId,
                credentialStore = data?.CredentialStore,
                linked = data?.Accounts.Select(a => new {a.AccountId, a.Login, a.Grants}),
                serverAccounts = ServerAccounts().Count,
                serverAccountsLoaded = ServerAccountsLoaded,
                requests = History.Requests().Take(20).Select(r => new {r.Type, r.AccountId, r.Result}),
                script = log
            }, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch
        {
            // best effort
        }
    }

    #region Preview

    /// <summary>
    ///     An in-memory integration for the modal previews: no files, no network, the given state, rows and history.
    /// </summary>
    /// <param name="invalidPasswordLogins">Normalized logins whose saved Steam password was rejected.</param>
    public static NebulaLinkIntegration CreatePreview(NebulaLinkState state, IReadOnlyList<MobileDataExtended> mafiles,
        IReadOnlyList<AchiefyAccount> server, IReadOnlyList<NebulaAccountLink> links, bool storeAvailable = true,
        NebulaModuleCatalog? catalog = null, Func<MobileDataExtended, MafileFileInfo?>? fileInfo = null,
        IReadOnlyCollection<string>? invalidPasswordLogins = null)
    {
        var store = new PreviewCredentialStore(storeAvailable);
        var integration = new NebulaLinkIntegration(new NebulaLinkOptions
        {
            DataDirectory = null,
            Http = new NoNetworkHttpFactory(),
            Selector = new DeviceCredentialStoreSelector(() => null, () => store),
            Mafiles = () => mafiles,
            SessionBackend = new PreviewSessionBackend(invalidPasswordLogins ?? []),
            ConfirmationTransport = new NoConfirmationTransport(),
            FileInfo = fileInfo
        });
        if (state is not (NebulaLinkState.Disabled or NebulaLinkState.Revoked))
        {
            integration.Links.Update(d =>
            {
                d.LinkId = Guid.Parse("4f9d2a51-7c3e-4b8a-9d61-2e5f8a7b3c10");
                d.TenantId = 1;
                d.TenantBaseUrl = "https://example.com/api";
                d.TenantLabel = "ID 1";
                d.AccountLabel = "trader@example.com";
                d.DeviceName = "MY-PC";
                d.PairedAt = DateTimeOffset.UtcNow.AddDays(-3);
                d.CredentialStore = IDeviceCredentialStore.KIND_OS;
                d.Paused = state == NebulaLinkState.Paused;
                d.Accounts = links.Select(l => l.Clone()).ToList();
            });
        }

        integration._mirror.PreviewSet(server);
        integration._session.PreviewSet(state, null, true, ModuleCatalogs.Sanitize(catalog ?? PreviewCatalog()));
        return integration;
    }

    /// <summary>The module catalog of the Tenant for previews.</summary>
    public static NebulaModuleCatalog PreviewCatalog()
    {
        return new NebulaModuleCatalog
        {
            Version = "preview",
            Modules =
            [
                Module("harvester", NebulaCategories.Trade, "Harvester"),
                Module("p2p_seller", NebulaCategories.Trade, "P2P Seller"),
                Module("trader", NebulaCategories.Trade, "Trader"),
                Module("steam_seller", NebulaCategories.MarketSell, "Steam Seller"),
                Module("steam_buyer", NebulaCategories.MarketBuy, "Steam Buyer")
            ]
        };

        // Feature names are English in every language
        static NebulaModuleInfo Module(string id, string category, string name)
        {
            return new NebulaModuleInfo {Id = id, Category = category, Names = new() {["en"] = name}};
        }
    }

    public void PreviewSetPairing(PendingPairing pairing)
    {
        _pairing.PreviewSet(pairing);
    }

    public void PreviewSetLinkFailure(int accountId, string result)
    {
        _linkService.SetLinkFailure(accountId, result);
    }

    public void PreviewSetStateReason(string reason)
    {
        _session.PreviewSetReason(reason);
    }

    public void PreviewSetServerNewer(bool newer)
    {
        _session.PreviewSetServerNewer(newer);
    }

    private sealed class NoNetworkHttpFactory : INebulaHttpFactory
    {
        public HttpMessageHandler CreateHandler()
        {
            throw new InvalidOperationException("Previews make no network requests");
        }
    }

    private sealed class PreviewCredentialStore(bool works) : IDeviceCredentialStore
    {
        public string Kind => IDeviceCredentialStore.KIND_OS;
        public string DisplayName => OperatingSystem.IsWindows() ? "Windows Credential Manager" : "OS keyring";
        public Task<bool> ProbeAsync() => Task.FromResult(works);
        public Task SaveAsync(Guid linkId, string credential) => Task.CompletedTask;
        public Task<string?> LoadAsync(Guid linkId) => Task.FromResult<string?>(null);
        public Task DeleteAsync(Guid linkId) => Task.CompletedTask;
    }

    private sealed class PreviewSessionBackend(IReadOnlyCollection<string> invalidPasswordLogins) : IHeadlessSessionBackend
    {
        public bool IsInvalidPasswordFlagged(MobileDataExtended mafile) =>
            invalidPasswordLogins.Contains(AccountMatcher.Normalize(mafile.AccountName));

        public void SetInvalidPasswordFlag(MobileDataExtended mafile, bool value)
        {
        }

        public string? GetStoredPassword(MobileDataExtended mafile) => null;
        public Task RefreshAsync(HttpClientHandlerPair chp, MobileDataExtended mafile) => Task.CompletedTask;
        public Task LoginAsync(HttpClientHandlerPair chp, MobileDataExtended mafile, string password) => Task.CompletedTask;

        public void OnSessionRenewed(MobileDataExtended mafile)
        {
        }
    }

    private sealed class NoConfirmationTransport : IConfirmationTransport
    {
        public string ProxyKey(MobileDataExtended mafile) => "direct";

        public Task<IReadOnlyList<Confirmation>> FetchAsync(MobileDataExtended mafile, SessionMode mode, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Confirmation>>([]);

        public Task<TransportSendResult> SendAsync(MobileDataExtended mafile, IReadOnlyList<Confirmation> items, bool accept,
            SessionMode mode, CancellationToken ct) =>
            Task.FromResult(new TransportSendResult(TransportSendStatus.Rejected));
    }

    #endregion
}
