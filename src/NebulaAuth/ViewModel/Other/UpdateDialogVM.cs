using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model.Update;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.Other;

public partial class UpdateDialogVM : ObservableObject
{
    public UpdateInfo Args { get; }
    public ChangelogEntry? Changelog { get; }
    public string? HtmlFallbackUrl { get; }
    public string Version => Args.CurrentVersion ?? string.Empty;
    public bool HasJsonChangelog => Changelog?.Changes?.Count > 0;

    [ObservableProperty] private bool _showRemindOptions;

    public UpdateDialogVM(UpdateInfo args, ChangelogEntry? changelog, string? htmlFallbackUrl)
    {
        Args = args;
        Changelog = changelog;
        HtmlFallbackUrl = htmlFallbackUrl;
    }

    [RelayCommand]
    private void UpdateNow()
    {
        DialogService.Close();
        // There is no cross-platform installer: open the release page so the user updates manually.
        // PlatformUtils never throws: a failure is logged and reported with a snackbar
        var url = Args.ReleasePageURL;
        if (string.IsNullOrEmpty(url)) return;
        PlatformUtils.OpenUrl(url);
    }

    [RelayCommand]
    private void ToggleRemindOptions()
    {
        ShowRemindOptions = !ShowRemindOptions;
    }

    [RelayCommand]
    private void SelectRemindOption(string option)
    {
        var delay = option switch
        {
            "1H" => TimeSpan.FromHours(1),
            "1D" => TimeSpan.FromDays(1),
            "3D" => TimeSpan.FromDays(3),
            "7D" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(1)
        };
        UpdateManager.SetRemindAfter(delay);
        DialogService.Close();
    }

    [RelayCommand]
    private void SkipVersion()
    {
        UpdateManager.SkipVersion(Version);
        DialogService.Close();
    }

    [RelayCommand]
    private void OpenLink(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        PlatformUtils.OpenUrl(url);
    }
}
