using Material.Icons;
using NebulaAuth.Core;

namespace NebulaAuth.View.Dialogs;

public partial class ConfirmCancelDialog
{
    /// <summary>
    ///     Preview (NEBULA_PREVIEW=NebulaAuth.View.Dialogs.ConfirmCancelDialog). NEBULA_PREVIEW_ARG:
    ///     "mafile" = remove mafile confirmation, "authenticator" = remove authenticator (red button),
    ///     anything else = the plain message dialog with that text.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        return arg switch
        {
            "mafile" => new ConfirmCancelDialog(RemoveMafileDemo("alphaecho")),
            "authenticator" => new ConfirmCancelDialog(RemoveAuthenticatorDemo("alphaecho")),
            _ => new ConfirmCancelDialog(string.IsNullOrEmpty(arg) ? "Remove the selected mafile?" : arg)
        };
    }

    // Same content as MainVM.RemoveMafile / MainVM.RemoveAuthenticator
    internal static ConfirmDialogOptions RemoveMafileDemo(string account)
    {
        return new ConfirmDialogOptions(Loc("RemoveMafileTitle"), account, MaterialIconKind.FileRemove,
            Loc("RemoveMafileText"), Loc("RemoveMafileHint"), HintBoxSeverity.Warning, Loc("RemoveMafileConfirm"));
    }

    internal static ConfirmDialogOptions RemoveAuthenticatorDemo(string account)
    {
        return new ConfirmDialogOptions(Loc("RemoveAuthenticatorTitle"), account, MaterialIconKind.ShieldRemove,
            Loc("RemoveAuthenticatorText"), Loc("RemoveAuthenticatorHint"), HintBoxSeverity.Error,
            Loc("RemoveAuthenticatorConfirm"), true);
    }

    private static string Loc(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, "MainVM", key);
    }
}
