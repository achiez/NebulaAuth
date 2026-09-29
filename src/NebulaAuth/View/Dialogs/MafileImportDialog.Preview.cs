#if DEBUG
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class MafileImportDialog
{
    /// <summary>
    ///     Debug preview: NEBULA_PREVIEW_ARG "conflicts" shows the conflict count and the group field expanded.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var vm = arg == "conflicts"
            ? new MafileImportDialogVM(["work", "personal", "farm-1"], 12, 3)
            {
                AddToGroup = true,
                Group = "farm-1"
            }
            : new MafileImportDialogVM(["work", "personal"], 5, 0);
        return new MafileImportDialog { DataContext = vm };
    }
}
#endif
