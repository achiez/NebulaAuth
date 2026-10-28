using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model.Mafiles.Protection;

namespace NebulaAuth.ViewModel;

public partial class MainVM //Skipped storage files (status bar badge)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkippedFiles), nameof(SkippedFilesHint))]
    private int _skippedFilesCount;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SkippedFilesHint))]
    private int _unprotectedSkippedCount;

    /// <summary>Some storage files were not loaded or not converted: the status bar shows a badge.</summary>
    public bool HasSkippedFiles => SkippedFilesCount > 0;

    public bool HasUnprotectedSkipped => UnprotectedSkippedCount > 0;

    public string SkippedFilesHint
    {
        get
        {
            var text = string.Format(LocManager.GetOrDefault("Skipped files: {0}", "SkippedFiles", "Snackbar"),
                SkippedFilesCount);
            if (HasUnprotectedSkipped)
            {
                text += " · " + string.Format(LocManager.GetOrDefault("Not protected: {0}", "SkippedFiles", "BadgeUnprotected"),
                    UnprotectedSkippedCount);
            }

            return text;
        }
    }

    partial void OnUnprotectedSkippedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnprotectedSkipped));
    }

    private void InitSkippedFiles()
    {
        MafileStorageService.Current.SkippedFilesChanged += () =>
            Dispatcher.UIThread.Post(UpdateSkippedFiles, DispatcherPriority.Background);
        UpdateSkippedFiles();
    }

    private void UpdateSkippedFiles()
    {
        var skipped = MafileStorageService.Current.SkippedFiles;
        SkippedFilesCount = skipped.Count;
        UnprotectedSkippedCount = skipped.Count(f => f.IsUnprotectedPlain);
    }

    [RelayCommand]
    private Task ShowSkippedFiles()
    {
        return DialogsController.ShowSkippedFilesDialog();
    }
}
