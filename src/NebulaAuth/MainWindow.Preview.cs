#if DEBUG
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NebulaAuth.Core;

namespace NebulaAuth;

public partial class MainWindow
{
    /// <summary>
    ///     Debug builds only: NEBULA_STYLE_GALLERY=1 replaces the main content with the style gallery
    ///     (every mapped control style in the current theme, theme/language switchers, exemplar dialogs).
    /// </summary>
    private void ShowStyleGalleryIfRequested()
    {
        if (Environment.GetEnvironmentVariable("NEBULA_STYLE_GALLERY") != "1") return;
        MainContent.Children.Clear();
        MainContent.Children.Add(new global::NebulaAuth.Theme.StyleGallery());
    }

    /// <summary>
    ///     Debug builds only, for screenshots without input: NEBULA_MAIN_DEMO is a comma-separated list of
    ///     "select" (select the first account), "search" (type "a" into the search box: the filter selects the first match),
    ///     "confirmations" (select the first account and show realistic fake confirmations of every type with placeholder
    ///     images, no network), "expand" (expand the grouped market listings), "maac" (accounts 2-4 with
    ///     auto-confirmation statuses), "blink" (update badge blink), "drop" (drag and drop overlay), "snackbar",
    ///     "narrow" (minimum window width), "proxy" (assign the first proxy of proxies.json to the selected account),
    ///     "menu" (open the first app bar menu), "submenu" (also its "Other" submenu), "accountmenu" (the "Account" menu),
    ///     "groups" / "proxies" (open the groups / proxy drop-down), "context" (account context menu),
    ///     "late" (open those popups 4 s after start),
    ///     "update" (update dialog with a fake 1.8.4 release + changelog, no network), "remove" / "unlink" (remove mafile /
    ///     remove authenticator confirmations; their results are ignored), "qr" / "qrrisk" / "qrminimal" / "qrexpired"
    ///     (select the first account and run the QR login on the current clipboard image up to the first network call,
    ///     fake session info in the dialog, see MainVM.DemoQrLogin), "copymafile" (select the first account and run "Copy
    ///     mafile" 2 s after start: in an encrypted storage it puts a readable temporary copy on the clipboard),
    ///     "pastemafiles" (import the files of the clipboard 2 s after start, like Ctrl+V), "closemain" (close the main
    ///     window 6 s after start: the normal app exit, e.g. deleting the readable "Copy mafile" copy).
    /// </summary>
    private void ShowMainDemoIfRequested()
    {
        var demo = Environment.GetEnvironmentVariable("NEBULA_MAIN_DEMO");
        if (string.IsNullOrWhiteSpace(demo)) return;
        var parts = demo.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is not { } vm) return;
            var qrScenario = parts.FirstOrDefault(p => p is "qr" or "qrrisk" or "qrminimal" or "qrexpired");
            if (parts.Contains("select") || parts.Contains("confirmations") || qrScenario != null)
                vm.SelectedMafile = vm.MaFiles.FirstOrDefault();
            if (parts.Contains("proxy"))
                vm.SelectedProxy = vm.Proxies.FirstOrDefault();
            if (parts.Contains("search"))
                vm.SearchText = "a";
            if (parts.Contains("confirmations"))
                vm.ShowDemoConfirmations();
            if (parts.Contains("maac"))
                vm.ShowDemoMaacStatuses();
            if (parts.Contains("blink"))
                StartUpdateBlink();
            if (parts.Contains("drop"))
                SetDropOverlayVisible(true);
            if (parts.Contains("narrow"))
                Width = MinWidth;
            if (parts.Contains("copymafile"))
                DispatcherTimer.RunOnce(() =>
                {
                    vm.SelectedMafile = vm.MaFiles.FirstOrDefault();
                    vm.CopyMafileCommand.Execute(vm.SelectedMafile);
                }, TimeSpan.FromSeconds(2));
            if (parts.Contains("pastemafiles"))
                DispatcherTimer.RunOnce(() => vm.PasteMafilesFromClipboardCommand.Execute(null), TimeSpan.FromSeconds(2));
            if (parts.Contains("closemain"))
                DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(6));
            if (parts.Contains("snackbar"))
                SnackbarController.SendSnackbar("Demo snackbar", TimeSpan.FromSeconds(30));
            // Popups: "late" delays them by 4 s (frame captures of the open animation start before the popup exists)
            void Popup(Action open)
            {
                if (parts.Contains("late")) DispatcherTimer.RunOnce(open, TimeSpan.FromSeconds(4));
                else Dispatcher.UIThread.Post(open, DispatcherPriority.Background);
            }

            if ((parts.Contains("menu") || parts.Contains("submenu")) && MainMenu.Items.FirstOrDefault() is MenuItem firstMenu)
                Popup(() =>
                {
                    firstMenu.Open();
                    if (parts.Contains("submenu") && firstMenu.Items.OfType<MenuItem>().LastOrDefault() is { } other)
                        DispatcherTimer.RunOnce(() => other.IsSubMenuOpen = true, TimeSpan.FromMilliseconds(600));
                });
            if (parts.Contains("accountmenu") && MainMenu.Items.ElementAtOrDefault(1) is MenuItem accountMenu)
                Popup(() => accountMenu.Open());
            if (parts.Contains("groups"))
                Popup(() => GroupsBox.IsDropDownOpen = true);
            if (parts.Contains("proxies"))
                Popup(() => ProxyBox.IsDropDownOpen = true);
            if (parts.Contains("context"))
                Popup(() => MafileContextMenu.Open(MafileListBox));
            // Dialogs with demo content (results are ignored: nothing is removed / downloaded)
            var account = vm.SelectedMafile?.AccountName ?? "alphaecho";
            if (parts.Contains("update"))
                _ = DialogService.Show(View.Dialogs.UpdateDialog.CreatePreview(null));
            else if (parts.Contains("remove"))
                _ = DialogService.Show(new View.Dialogs.ConfirmCancelDialog(
                    View.Dialogs.ConfirmCancelDialog.RemoveMafileDemo(account)));
            else if (parts.Contains("unlink"))
                _ = DialogService.Show(new View.Dialogs.ConfirmCancelDialog(
                    View.Dialogs.ConfirmCancelDialog.RemoveAuthenticatorDemo(account)));
            if (qrScenario != null)
                _ = vm.DemoQrLogin(qrScenario);
            if (parts.Contains("expand"))
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var expander in ConfirmationsList.GetVisualDescendants().OfType<Expander>())
                        expander.IsExpanded = true;
                }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }
}
#endif
