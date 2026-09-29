using CommunityToolkit.Mvvm.ComponentModel;

namespace NebulaAuth.Model.Update;

/// <summary>
///     Observable state of the update flow (<see cref="Core.UpdateManager.Status" />), shown by the update dialog.
///     Changed on the UI thread only.
/// </summary>
public partial class UpdateStatus : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsAvailable), nameof(IsDownloading), nameof(IsReadyToRestart),
        nameof(IsFailed), nameof(ShowChangelog), nameof(ShowInstallAction), nameof(ShowOpenPageAction),
        nameof(ShowRetryAction))]
    private UpdateState _state = UpdateState.Idle;

    /// <summary>Version of the running application ("2.0.0").</summary>
    [ObservableProperty] private string _installedVersion = string.Empty;

    /// <summary>Latest version from the signed feed, null before a successful check.</summary>
    [ObservableProperty] private string? _availableVersion;

    /// <summary>Size of the package that will be downloaded (full package), 0 when unknown.</summary>
    [ObservableProperty] private long _downloadSize;

    [ObservableProperty] private ChangelogEntry? _changelog;

    /// <summary>HTML changelog page, used when the JSON changelog could not be loaded.</summary>
    [ObservableProperty] private string? _changelogUrl;

    /// <summary>0-100 while <see cref="UpdateState.Downloading" />.</summary>
    [ObservableProperty] private int _progress;

    /// <summary>
    ///     True when this installation can update itself (Velopack-managed). False for development builds and folders
    ///     that were not installed from a release package: the dialog offers the download page instead.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallAction), nameof(ShowOpenPageAction))]
    private bool _canInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetryAction))]
    private UpdateFailureKind _failureKind;

    /// <summary>Technical detail of the failure (path, exception message), shown in small text and logged.</summary>
    [ObservableProperty] private string? _failureDetail;

    /// <summary>Package for a manual update on this OS/architecture (portable ZIP / AppImage), or the release page.</summary>
    [ObservableProperty] private string? _manualUrl;

    /// <summary>Release page of <see cref="AvailableVersion" /> (or the releases list).</summary>
    [ObservableProperty] private string? _releasePageUrl;

    public bool IsChecking => State == UpdateState.Checking;
    public bool IsAvailable => State == UpdateState.Available;
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsReadyToRestart => State == UpdateState.ReadyToRestart;
    public bool IsFailed => State == UpdateState.Failed;

    public bool ShowChangelog => State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToRestart;
    public bool ShowInstallAction => State == UpdateState.Available && CanInstall;
    public bool ShowOpenPageAction => State == UpdateState.Available && !CanInstall;

    /// <summary>Retrying makes sense unless the cause is permanent for this folder.</summary>
    public bool ShowRetryAction => State == UpdateState.Failed && CanInstall &&
                                   FailureKind is not (UpdateFailureKind.NoWriteAccess or UpdateFailureKind.Signature);
}
