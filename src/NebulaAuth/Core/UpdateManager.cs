using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Threading;
using NebulaAuth.Model;
using NebulaAuth.Model.Update;
using Newtonsoft.Json;

namespace NebulaAuth.Core;

/// <summary>
///     Update check (AutoUpdater.NET replacement). Only the check + changelog dialog are implemented,
///     installing an update opens the release page in the browser (see <see cref="UpdateInfo.ReleasePageURL" />).
/// </summary>
public static class UpdateManager
{
    private const string BASE_URL =
        "https://raw.githubusercontent.com/achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies/master/";

    private const string UPDATE_URL = BASE_URL + "NebulaAuth/update.xml";

    private const string CHANGELOG_BASE_URL = BASE_URL + "changelog/";

    private static readonly HttpClient HttpClient = new();

    public static bool HasPendingUpdate { get; private set; }

    public static event Action? PendingUpdateDetected;

    public static void CheckForUpdates(bool manual = false)
    {
        _ = CheckForUpdatesAsync(manual);
    }

    public static void SkipVersion(string version)
    {
        var settings = UpdateSettings.Load();
        settings.SkipVersion(version);
    }

    public static void SetRemindAfter(TimeSpan delay)
    {
        var settings = UpdateSettings.Load();
        settings.SetRemindAfter(DateTime.Now + delay);
    }

    private static async Task CheckForUpdatesAsync(bool isManual)
    {
        try
        {
            var args = await FetchUpdateInfoAsync().ConfigureAwait(false);
            await HandleCheckForUpdateResult(args, isManual).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Shell.Logger.Error(e, "Error while checking updates");
        }
    }

    private static async Task HandleCheckForUpdateResult(UpdateInfo args, bool isManual)
    {
        if (args.Error != null)
        {
            Shell.Logger.Debug(args.Error, "Update check failed");
            if (isManual)
            {
                Dispatcher.UIThread.Invoke(() =>
                    SnackbarController.SendSnackbar(LocManager.GetCommonOrDefault("Request error",
                        "RequestError")));
            }

            return;
        }

        if (!args.IsUpdateAvailable)
        {
            if (isManual)
            {
                Dispatcher.UIThread.Invoke(() =>
                    SnackbarController.SendSnackbar(
                        LocManager.GetCodeBehindOrDefault("You are using the latest version", "UpdateService",
                            "LatestVersion")));
            }

            return;
        }

        var version = args.CurrentVersion!;
        var settings = UpdateSettings.Load();

        if (!isManual && !settings.ShouldShow(version))
        {
            HasPendingUpdate = true;
            Dispatcher.UIThread.Invoke(() => PendingUpdateDetected?.Invoke());
            return;
        }

        ChangelogEntry? changelog = null;
        try
        {
            var jsonUrl = $"{CHANGELOG_BASE_URL}{version}.json";
            var response = await HttpClient.GetAsync(jsonUrl).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                changelog = JsonConvert.DeserializeObject<ChangelogEntry>(json);
            }
        }
        catch
        {
            // fallback to HTML changelog URL
        }

        var htmlFallbackUrl = changelog == null ? args.ChangelogURL : null;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await DialogsController.ShowUpdateDialog(args, changelog, htmlFallbackUrl);
        });
    }

    /// <summary>
    ///     Downloads and parses update.xml (AutoUpdater.NET format) and compares it with the running version
    ///     the same way AutoUpdater.NET did (<c>new Version(xml version) &gt; entry assembly version</c>).
    /// </summary>
    private static async Task<UpdateInfo> FetchUpdateInfoAsync()
    {
        var installedVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        try
        {
            var xml = await HttpClient.GetStringAsync(UPDATE_URL).ConfigureAwait(false);
            var item = XDocument.Parse(xml).Root ?? throw new FormatException("update.xml has no root element");
            var versionStr = item.Element("version")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(versionStr))
                throw new FormatException("update.xml has no version");

            var currentVersion = new Version(versionStr);
            var checksum = item.Element("checksum");
            bool.TryParse(item.Element("mandatory")?.Value.Trim(), out var mandatory);
            return new UpdateInfo
            {
                CurrentVersion = versionStr,
                InstalledVersion = installedVersion,
                DownloadURL = item.Element("url")?.Value.Trim(),
                ChangelogURL = item.Element("changelog")?.Value.Trim(),
                Mandatory = mandatory,
                Checksum = checksum?.Value.Trim(),
                HashingAlgorithm = checksum?.Attribute("algorithm")?.Value,
                IsUpdateAvailable = installedVersion != null && currentVersion > installedVersion
            };
        }
        catch (Exception ex)
        {
            return new UpdateInfo
            {
                InstalledVersion = installedVersion,
                Error = ex
            };
        }
    }
}
