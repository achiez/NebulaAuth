#if DEBUG
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class SetCryptPasswordDialog
{
    internal static object CreatePreview(string? arg)
    {
        return new SetCryptPasswordDialog { DataContext = new SetEncryptPasswordVM { Password = arg ?? "hunter2" } };
    }
}
#endif
