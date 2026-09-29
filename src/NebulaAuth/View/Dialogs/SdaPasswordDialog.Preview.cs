#if DEBUG
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class SdaPasswordDialog
{
    internal static object CreatePreview(string? arg)
    {
        var vm = new SdaPasswordDialogVM();
        if (!string.IsNullOrEmpty(arg)) vm.Password = arg;
        return new SdaPasswordDialog { DataContext = vm };
    }
}
#endif
