using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class MafileImportDialog
{
    /// <summary>
    ///     NEBULA_PREVIEW_ARG "conflicts" shows the conflict count and the group field expanded,
    ///     "errors" conflicts + invalid files, "errorsonly" invalid files without conflicts.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var vm = arg switch
        {
            "conflicts" => new MafileImportDialogVM(["work", "personal", "farm-1"], new MafileImportCounts(12, 12, 3, 0))
            {
                AddToGroup = true,
                Group = "farm-1"
            },
            "errors" => new MafileImportDialogVM(["work", "personal", "farm-1"], new MafileImportCounts(14, 11, 3, 3)),
            "errorsonly" => new MafileImportDialogVM(["work", "personal"], new MafileImportCounts(6, 5, 0, 1)),
            _ => new MafileImportDialogVM(["work", "personal"], new MafileImportCounts(5, 5, 0, 0))
        };
        return new MafileImportDialog {DataContext = vm};
    }
}
