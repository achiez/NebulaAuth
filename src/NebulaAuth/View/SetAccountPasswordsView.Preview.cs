#if DEBUG
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View;

public partial class SetAccountPasswordsView
{
    /// <summary>
    ///     Debug preview (NEBULA_PREVIEW=NebulaAuth.View.SetAccountPasswordsView, NEBULA_PREVIEW_ARG=state): default/"1"
    ///     no encryption password set yet, empty accounts field; "2" encryption password already set. Suffix "f"
    ///     pre-fills the accounts textbox with sample login:password lines; suffix "t" shows a sample result in the
    ///     HintBox. No network, no real accounts, no mafile storage access.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        arg = (arg ?? "1").Trim().ToLowerInvariant();
        var vm = new SetAccountPasswordsVM
        {
            IsEncryptionPasswordSet = arg.Contains('2')
        };
        if (arg.Contains('f'))
        {
            vm.AccountsPasswords = "preview_login1:preview_password1\npreview_login2:preview_password2";
        }

        if (arg.Contains('t'))
        {
            vm.Tip = "Success: 2, Not found: 0, Errors: 0";
        }

        return new SetAccountPasswordsView {DataContext = vm};
    }
}
#endif
