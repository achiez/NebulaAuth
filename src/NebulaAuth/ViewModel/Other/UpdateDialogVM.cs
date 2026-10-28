using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model.Update;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.Other;

/// <summary>
///     Update dialog over the shared <see cref="UpdateStatus" />: the dialog can be closed during the download and
///     reopened later with the current state. Dispose after the dialog closed (unsubscribes from the status).
/// </summary>
public partial class UpdateDialogVM : ObservableObject, IDisposable
{
    [ObservableProperty] private bool _showRemindOptions;

    public UpdateDialogVM(UpdateStatus status)
    {
        Status = status;
        Status.PropertyChanged += Status_OnPropertyChanged;
    }

    public UpdateStatus Status { get; }

    public string Version => Status.AvailableVersion ?? string.Empty;
    public ChangelogEntry? Changelog => Status.Changelog;
    public bool HasJsonChangelog => Status.ShowChangelog && Status.Changelog?.Changes?.Count > 0;
    public bool ShowHtmlChangelog => Status.ShowChangelog && !HasJsonChangelog;
    public string? HtmlFallbackUrl => Status.ChangelogUrl;

    public string HeaderTitle => Status.State switch
    {
        UpdateState.Failed => Tr("FailedTitle", "Update failed"),
        UpdateState.ReadyToRestart => Tr("ReadyTitle", "Update ready:"),
        _ => Tr("Title", "New version available:")
    };

    /// <summary>
    ///     Size of the download in MB ("≈ 62 MB"), empty when unknown.
    /// </summary>
    public string DownloadSizeText => Status.DownloadSize > 0
        ? $"≈ {Math.Max(1, (int) Math.Round(Status.DownloadSize / 1048576.0))} MB"
        : string.Empty;

    public string FailureText => Tr("Failure." + Status.FailureKind, Status.FailureKind.ToString());

    public void Dispose()
    {
        Status.PropertyChanged -= Status_OnPropertyChanged;
        GC.SuppressFinalize(this);
    }

    private void Status_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UpdateStatus.State):
                if (Status.State != UpdateState.Available) ShowRemindOptions = false;
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(HasJsonChangelog));
                OnPropertyChanged(nameof(ShowHtmlChangelog));
                OnPropertyChanged(nameof(FailureText));
                break;
            case nameof(UpdateStatus.AvailableVersion):
                OnPropertyChanged(nameof(Version));
                break;
            case nameof(UpdateStatus.Changelog):
                OnPropertyChanged(nameof(Changelog));
                OnPropertyChanged(nameof(HasJsonChangelog));
                OnPropertyChanged(nameof(ShowHtmlChangelog));
                break;
            case nameof(UpdateStatus.ChangelogUrl):
                OnPropertyChanged(nameof(HtmlFallbackUrl));
                break;
            case nameof(UpdateStatus.DownloadSize):
                OnPropertyChanged(nameof(DownloadSizeText));
                break;
            case nameof(UpdateStatus.FailureKind):
                OnPropertyChanged(nameof(FailureText));
                break;
        }
    }

    private static string Tr(string key, string fallback)
    {
        return LocManager.GetOrDefault(fallback, "UpdateDialog", key);
    }

    /// <summary>
    ///     "Download and install": the dialog stays open and shows the progress.
    /// </summary>
    [RelayCommand]
    private void Install()
    {
        UpdateManager.StartDownload();
    }

    /// <summary>
    ///     This copy can't update itself (not installed from a release package): open the release page.
    /// </summary>
    [RelayCommand]
    private void OpenDownloadPage()
    {
        DialogService.Close();
        var url = Status.ReleasePageUrl;
        if (!string.IsNullOrEmpty(url)) PlatformUtils.OpenUrl(url);
    }

    [RelayCommand]
    private void CancelDownload()
    {
        UpdateManager.CancelDownload();
    }

    [RelayCommand]
    private void RestartAndUpdate()
    {
        UpdateManager.ApplyAndRestart();
    }

    [RelayCommand]
    private void Retry()
    {
        DialogService.Close();
        UpdateManager.Retry();
    }

    [RelayCommand]
    private void DownloadManually()
    {
        var url = Status.ManualUrl ?? Status.ReleasePageUrl;
        if (!string.IsNullOrEmpty(url)) PlatformUtils.OpenUrl(url);
    }

    [RelayCommand]
    private void Close()
    {
        DialogService.Close();
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
