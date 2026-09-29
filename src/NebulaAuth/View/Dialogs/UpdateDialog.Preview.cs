#if DEBUG
using System;
using NebulaAuth.Core;
using NebulaAuth.Model.Update;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class UpdateDialog
{
    /// <summary>
    ///     Debug preview (NEBULA_PREVIEW=NebulaAuth.View.Dialogs.UpdateDialog). NEBULA_PREVIEW_ARG:
    ///     empty = JSON changelog of 1.8.4, "remind" = same with the remind-after options open,
    ///     "short" = only the first 3 changes, "html" = no JSON changelog but an HTML changelog link,
    ///     "none" = no changelog at all.
    ///     Optional suffix "@h&lt;height&gt;" (e.g. "@h600", "remind@h600") resizes the main window first, to check that
    ///     the actions stay reachable on a short window.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        if (arg != null && arg.IndexOf("@h", StringComparison.Ordinal) is var at and >= 0)
        {
            if (int.TryParse(arg[(at + 2)..], out var height) && AppHost.MainWindow is { } window)
                window.Height = height;
            arg = arg[..at];
        }

        const string repo = "https://github.com/achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies";
        var info = new UpdateInfo
        {
            CurrentVersion = "1.8.4",
            InstalledVersion = new Version(1, 8, 3, 0),
            DownloadURL = repo + "/releases/download/1.8.4/NebulaAuth.1.8.4.zip",
            ChangelogURL = "https://achiez.github.io/NebulaAuth-Steam-Desktop-Authenticator-by-Achies/changelog/1.8.4.html",
            IsUpdateAvailable = true
        };
        // Content of changelog/1.8.4.json
        var changelog = new ChangelogEntry
        {
            Version = "1.8.4",
            Date = "2026-03-14",
            Changes =
            [
                new ChangeItem
                {
                    Type = "FIX",
                    Text = "Rebuilt release package to include the missing NebulaAuth.exe file. Version 1.8.4 is functionally " +
                           "identical to 1.8.3 and only fixes the packaging issue that caused the executable to be absent in " +
                           "the previous release."
                },
                new ChangeItem
                {
                    Type = "NEW",
                    Text = "Introduced a redesigned update system with a custom update dialog and integrated changelog viewer",
                    Link = repo + "/pull/19"
                },
                new ChangeItem
                {
                    Type = "NEW",
                    Text = "Added support for importing SDA-encrypted mafiles with automatic manifest detection",
                    Link = repo + "/pull/17"
                },
                new ChangeItem
                {
                    Type = "NEW",
                    Text = "Grouped market confirmations can now be expanded to reveal individual items",
                    Link = repo + "/pull/18"
                },
                new ChangeItem
                {
                    Type = "SECURITY",
                    Text = "Added SHA256 checksum verification for downloaded update packages"
                },
                new ChangeItem
                {
                    Type = "IMPROVEMENT",
                    Text = "Export feature now trims input automatically to prevent issues caused by invisible characters " +
                           "or extra spaces"
                },
                new ChangeItem
                {
                    Type = "IMPROVEMENT",
                    Text = "Improved update experience with 'Remind later' and 'Skip version' options"
                },
                new ChangeItem
                {
                    Type = "IMPROVEMENT",
                    Text = "Added visual update indicator and manual 'Check for updates' action"
                },
                new ChangeItem
                {
                    Type = "IMPROVEMENT",
                    Text = "Expanded localization support with Spanish, Turkish and Kazakh languages"
                },
                new ChangeItem
                {
                    Type = "INFO",
                    Text = "Read the full release notes for NebulaAuth 1.8.3",
                    Link = "https://teletype.in/@achies_raw/o-RDwZKZkAU"
                }
            ]
        };

        var vm = arg switch
        {
            "html" => new UpdateDialogVM(info, null, info.ChangelogURL),
            "none" => new UpdateDialogVM(info, null, null),
            _ => new UpdateDialogVM(info, changelog, null)
        };
        if (arg == "remind") vm.ShowRemindOptions = true;
        if (arg == "short") changelog.Changes.RemoveRange(3, changelog.Changes.Count - 3);
        return new UpdateDialog {DataContext = vm};
    }
}
#endif
