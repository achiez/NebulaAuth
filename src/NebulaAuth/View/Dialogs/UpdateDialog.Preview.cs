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
    ///     empty = available update with the JSON changelog of 1.8.4 (installable), "page" = same for a copy that can't
    ///     update itself (download page), "remind" = remind-after options open, "short" = only the first 3 changes,
    ///     "html" = no JSON changelog but a changelog link, "none" = no changelog at all, "progress" = downloading (45%),
    ///     "ready" = downloaded, "failed:&lt;UpdateFailureKind&gt;" (e.g. "failed:NoWriteAccess") = failure.
    ///     Optional suffix "@h&lt;height&gt;" (e.g. "@h600", "remind@h600") resizes the main window first, to check that
    ///     the actions stay reachable on a short window.
    ///     The preview uses its own <see cref="UpdateStatus" />: the real update state is not changed.
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
        var status = new UpdateStatus
        {
            InstalledVersion = "2.0.0",
            AvailableVersion = "2.0.1",
            DownloadSize = 61_790_670,
            CanInstall = arg != "page",
            ReleasePageUrl = repo + "/releases/tag/v2.0.1",
            ManualUrl = repo + "/releases/download/v2.0.1/NebulaAuth-win-x64-Portable.zip",
            ChangelogUrl = repo + "/releases/tag/v2.0.1"
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

        if (arg == "short") changelog.Changes.RemoveRange(3, changelog.Changes.Count - 3);
        status.Changelog = arg is "html" or "none" ? null : changelog;
        if (arg == "none") status.ChangelogUrl = null;
        status.State = UpdateState.Available;
        switch (arg)
        {
            case "progress":
                status.Progress = 45;
                status.State = UpdateState.Downloading;
                break;
            case "ready":
                status.Progress = 100;
                status.State = UpdateState.ReadyToRestart;
                break;
            case not null when arg.StartsWith("failed", StringComparison.Ordinal):
                var kindText = arg.Length > 7 ? arg[7..] : nameof(UpdateFailureKind.Network);
                status.FailureKind = Enum.TryParse<UpdateFailureKind>(kindText, true, out var kind)
                    ? kind
                    : UpdateFailureKind.Network;
                status.FailureDetail = status.FailureKind == UpdateFailureKind.NoWriteAccess
                    ? @"C:\Program Files\NebulaAuth"
                    : "HttpRequestException: No connection could be made because the target machine actively refused it.";
                status.State = UpdateState.Failed;
                break;
        }

        // The preview dialog is not disposed by DialogsController: its status is not shared, nothing leaks
        var vm = new UpdateDialogVM(status);
        if (arg == "remind") vm.ShowRemindOptions = true;
        return new UpdateDialog {DataContext = vm};
    }
}
#endif
