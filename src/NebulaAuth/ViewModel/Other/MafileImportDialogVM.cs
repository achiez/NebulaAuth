using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NebulaAuth.ViewModel.Other;

public partial class MafileImportDialogVM : ObservableObject
{
    public ObservableCollection<string> Groups { get; }

    /// <summary>Files that can be imported.</summary>
    public int TotalCount { get; }

    /// <summary>All selected files (valid + errors).</summary>
    public int SelectedCount { get; }

    public int ConflictCount { get; }
    public int ErrorCount { get; }
    public bool HasConflicts => ConflictCount > 0;
    public bool HasErrors => ErrorCount > 0;
    [ObservableProperty] private bool _addToGroup;
    [ObservableProperty] private string? _group;
    [ObservableProperty] private bool _overwriteConflicts;

    public MafileImportDialogVM(IEnumerable<string> groups, MafileImportCounts counts)
    {
        Groups = new ObservableCollection<string>(groups);
        SelectedCount = counts.Selected;
        TotalCount = counts.Valid;
        ConflictCount = counts.Conflicts;
        ErrorCount = counts.Errors;
    }
}

/// <param name="Selected">All selected files.</param>
/// <param name="Valid">Files that can be imported.</param>
/// <param name="Conflicts">Valid files whose mafile already exists.</param>
/// <param name="Errors">Invalid / unsupported / unreadable files (not imported).</param>
public record MafileImportCounts(int Selected, int Valid, int Conflicts, int Errors);

public record MafileImportDialogResult(string? Group, bool OverwriteConflicts);
