using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.Model;
using NebulaAuth.NebulaLink;
using Newtonsoft.Json;

namespace NebulaAuth.Core;

/// <summary>
///     links.json of the NebulaAuth repository, the only source of these addresses: website and documentation links, the
///     Nebula Link pages of Achiefy and the Achiefy API bases Nebula Link may pair with (<see cref="NebulaLinkApis" />;
///     unavailable when the file can't be fetched).
/// </summary>
public static partial class LinksManager
{
    // The bootstrap address: links.json itself can't come from links.json

    private const string LINKS_URL =
        "https://raw.githubusercontent.com/achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies/master/NebulaAuth/links.json";

    private static readonly HttpClient HttpClient = new() {Timeout = TimeSpan.FromSeconds(20)};
    private static int _fetching;

    public static string? WebsiteUrl { get; private set; }
    public static string? TelegramUrl { get; private set; }
    public static string? GitHubUrl { get; private set; }
    public static string? ReleasesUrl { get; private set; }
    public static string? NebulaLinkCabinetUrl { get; private set; }
    public static string? NebulaLinkDataUrl { get; private set; }

    private static string? _documentation;
    private static string? _linkingTroubleshooting;

    /// <summary>The documentation in the UI language; null when links.json has none.</summary>
    public static string? DocumentationUrl => InDocsLanguage(_documentation);

    /// <summary>The linking troubleshooting guide in the UI language; null when links.json has none.</summary>
    public static string? LinkingTroubleshootingUrl => InDocsLanguage(_linkingTroubleshooting);

    // The documentation exists in Russian and English only
    private static string? InDocsLanguage(string? url)
    {
        var language = LocManager.GetCurrentLanguageCode() == "ru" ? "ru" : "en";
        return url?.Replace("{lang}", language, StringComparison.Ordinal);
    }

    public static async Task FetchAsync()
    {
        if (Interlocked.Exchange(ref _fetching, 1) == 1) return;
        NebulaLinkApis.SetLoading();
        string[]? apis = null;
        try
        {
            var json = await HttpClient.GetStringAsync(LINKS_URL).ConfigureAwait(false);
            var links = JsonConvert.DeserializeObject<RemoteLinks>(json);
            WebsiteUrl = links?.Website;
            _documentation = links?.LocalizedDocumentation ?? links?.Documentation;
            TelegramUrl = links?.Telegram;
            GitHubUrl = links?.GitHub;
            ReleasesUrl = links?.Releases;
            _linkingTroubleshooting = links?.LinkingTroubleshooting;
            NebulaLinkCabinetUrl = links?.NebulaLinkCabinet;
            NebulaLinkDataUrl = links?.NebulaLinkData;
            apis = links?.NebulaLinkApis;
        }
        catch (Exception ex)
        {
            Shell.Logger.Debug(ex, "Failed to fetch remote links");
        }
        finally
        {
            DebugOverrideApis(ref apis);
            NebulaLinkApis.SetFetched(apis);
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    static partial void DebugOverrideApis(ref string[]? apis);
}
