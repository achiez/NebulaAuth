using System;
using System.Text.RegularExpressions;

namespace NebulaAuth.Model.Update;

/// <summary>
///     Result of an update check (replacement for AutoUpdater.NET <c>UpdateInfoEventArgs</c>, same property names).
///     Parsed from the AutoUpdater XML format (<c>NebulaAuth/update.xml</c>).
/// </summary>
public partial class UpdateInfo
{
    /// <summary>
    ///     Latest available version as written in update.xml (e.g. "1.8.4").
    /// </summary>
    public string? CurrentVersion { get; init; }

    /// <summary>
    ///     Version of the running application.
    /// </summary>
    public Version? InstalledVersion { get; init; }

    /// <summary>
    ///     Direct download URL of the update package (&lt;url&gt;).
    /// </summary>
    public string? DownloadURL { get; init; }

    /// <summary>
    ///     HTML changelog URL (&lt;changelog&gt;).
    /// </summary>
    public string? ChangelogURL { get; init; }

    public bool Mandatory { get; init; }

    /// <summary>
    ///     Checksum of the package (&lt;checksum&gt;) and its algorithm attribute.
    /// </summary>
    public string? Checksum { get; init; }

    public string? HashingAlgorithm { get; init; }

    public bool IsUpdateAvailable { get; init; }

    /// <summary>
    ///     Not null when the update information couldn't be retrieved or parsed.
    /// </summary>
    public Exception? Error { get; init; }

    /// <summary>
    ///     Page to open for a manual update: the GitHub release page when <see cref="DownloadURL" /> is a GitHub release
    ///     asset (".../releases/download/{tag}/file"), otherwise <see cref="DownloadURL" /> itself.
    /// </summary>
    public string? ReleasePageURL
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DownloadURL)) return null;
            var match = GitHubReleaseAssetRegex().Match(DownloadURL);
            return match.Success
                ? $"{match.Groups["repo"].Value}/releases/tag/{match.Groups["tag"].Value}"
                : DownloadURL;
        }
    }

    [GeneratedRegex(@"^(?<repo>https?://github\.com/[^/]+/[^/]+)/releases/download/(?<tag>[^/]+)/",
        RegexOptions.IgnoreCase)]
    private static partial Regex GitHubReleaseAssetRegex();
}
