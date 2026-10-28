using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View;

public partial class SettingsView
{
    private const string PREVIEW_MASTER_PASSWORD = "preview master password";
    private const string PREVIEW_NON_LATIN_PASSWORD = "пароль master 2026";

    /// <summary>
    ///     Preview (NEBULA_PREVIEW=NebulaAuth.View.SettingsView). NEBULA_PREVIEW_ARG, comma separated:
    ///     <c>theme</c> - open the Theme tab; <c>rename</c> - show a sample rename result;
    ///     <c>drag</c> - Theme tab in the "slider is dragged" state (transparent dialog);
    ///     <c>live</c> - after opening, switches theme to Luxury and language to Russian through the VM (live switch check).
    ///     Encryption tab (the real storage of the data folder): <c>enc</c> - overview; <c>enc-enable</c> /
    ///     <c>enc-change</c> / <c>enc-disable</c> / <c>enc-skipped</c> - that page (forms pre-filled with a sample
    ///     password); <c>enc-mismatch</c> - enable form with the "passwords do not match" error; <c>enc-change-wrong</c> -
    ///     change form after a wrong current password; <c>enc-run-enable</c> / <c>enc-run-disable</c> /
    ///     <c>enc-run-resume</c> - starts that operation (really converts the files of the data folder; slow it down with
    ///     NEBULA_DEBUG_STORAGE_SLOW=&lt;ms&gt; for progress screenshots). The sample password is
    ///     "preview master password". <c>enc-saveoff</c> - switches "Save Steam passwords" off (confirmation page when
    ///     passwords are saved); <c>enc-forget</c> - also confirms it (deletes the saved passwords of the data folder).
    ///     <c>enc-as-encrypting</c> / <c>enc-as-decrypting</c> - the tab shows that unfinished mode instead of the real one;
    ///     <c>enc-nonlatin</c> - the enable / change form is filled with a Cyrillic password (layout hint). Caps Lock hint:
    ///     NEBULA_DEBUG_PASSWORD_HINTS=caps (the focused field).
    ///     <c>integrations</c> - the main tab scrolled to the Achiefy group (Nebula Link).
    ///     Devices tab (changes the model list of the data folder's settings.json): <c>devices</c> - the tab;
    ///     <c>devices-add</c> - adds "My Galaxy S21"; <c>devices-removed</c> - removes three built-in models (restore link);
    ///     <c>devices-one</c> - removes every model but one (SM-S911B when listed); <c>devices-error</c> - tries to add
    ///     "Pixel 8" (duplicate error while it is listed); <c>devices-bottom</c> - the model list scrolled to the end.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var args = (arg ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vm = new SettingsVM();
        var view = new SettingsView {DataContext = vm};
        if (args.Contains("theme") || args.Contains("drag"))
            vm.SelectedTab = 1;
        if (args.Any(a => a.StartsWith("enc", StringComparison.Ordinal)))
            vm.SelectedTab = SettingsVM.ENCRYPTION_TAB;
        PreviewEncryption(view, vm.Encryption, args);
        PreviewDevices(view, vm, args);
        // "integrations": the main tab scrolled to the Achiefy group (Nebula Link)
        if (args.Contains("integrations"))
            view.Loaded += (_, _) => DispatcherTimer.RunOnce(() => view.MainSettingsScroll.ScrollToEnd(),
                TimeSpan.FromMilliseconds(400));
        if (args.Contains("live"))
            view.Loaded += (_, _) => DispatcherTimer.RunOnce(() =>
            {
                vm.ThemeType = ThemeType.Luxury;
                vm.Language = LocalizationLanguage.Russian;
            }, TimeSpan.FromSeconds(2));
        if (args.Contains("rename"))
        {
            var format = LocManager.GetCodeBehindOrDefault("{0} {1} {2} {3} {4}", "SettingsVM", "MafileRenaming",
                "PartialSuccess");
            vm.RenameResultText = string.Format(format, 12, 10, 1, 1, "mafiles_backup_2026-09-26.zip");
        }

        if (args.Contains("drag"))
            view.Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                view.BeginSliderDrag();
                // Keep the drag look for the screenshot when the window is not in the foreground
                if (view._dragTopLevel is WindowBase window)
                    window.Deactivated -= view.Window_OnDeactivated;
            }, DispatcherPriority.Background);
        return view;
    }

    private static void PreviewDevices(SettingsView view, SettingsVM vm, string[] args)
    {
        if (!args.Any(a => a.StartsWith("devices", StringComparison.Ordinal))) return;
        vm.SelectedTab = SettingsVM.DEVICES_TAB;
        var devices = vm.Devices;
        if (args.Contains("devices-add"))
        {
            devices.NewModel = "My Galaxy S21";
            devices.AddCommand.Execute(null);
        }

        if (args.Contains("devices-removed"))
            foreach (var name in new[] {"Pixel 7", "Pixel 7a", "SM-S901B"})
                devices.RemoveCommand.Execute(devices.Items.FirstOrDefault(i => i.Name == name));

        if (args.Contains("devices-one"))
        {
            var keep = devices.Items.FirstOrDefault(i => i.Name == "SM-S911B") ?? devices.Items[0];
            foreach (var item in devices.Items.Where(i => i != keep).ToList())
                devices.RemoveCommand.Execute(item);
        }

        if (args.Contains("devices-error"))
        {
            devices.NewModel = "Pixel 8";
            devices.AddCommand.Execute(null);
        }

        if (args.Contains("devices-bottom"))
            view.Loaded += (_, _) => DispatcherTimer.RunOnce(() => view.DeviceModelsScroll.ScrollToEnd(),
                TimeSpan.FromMilliseconds(500));
    }

    private static void PreviewEncryption(SettingsView view, EncryptionSettingsVM enc, string[] args)
    {
        if (args.Contains("enc-enable") || args.Contains("enc-mismatch")) enc.ShowEnableCommand.Execute(null);
        if (args.Contains("enc-change") || args.Contains("enc-change-wrong")) enc.ShowChangeCommand.Execute(null);
        if (args.Contains("enc-disable")) enc.ShowDisableCommand.Execute(null);
        if (args.Contains("enc-skipped")) enc.ShowSkippedCommand.Execute(null);
        if (args.Contains("enc-as-encrypting")) enc.DebugForcedMode = StorageMode.Encrypting;
        if (args.Contains("enc-as-decrypting")) enc.DebugForcedMode = StorageMode.Decrypting;
        if (enc.DebugForcedMode != null) enc.DebugRefresh();
        if (enc.IsEnablePage || enc.IsChangePage)
        {
            var password = args.Contains("enc-nonlatin") ? PREVIEW_NON_LATIN_PASSWORD : PREVIEW_MASTER_PASSWORD;
            enc.NewPassword = password;
            enc.ConfirmPassword = args.Contains("enc-mismatch") ? "preview master pasword" : password;
            enc.CurrentPassword = args.Contains("enc-change-wrong") ? "wrong password" : string.Empty;
        }

        if (args.Contains("enc-mismatch")) enc.EnableCommand.Execute(null);
        if (args.Contains("enc-change-wrong")) RunAfterLoaded(() => enc.ChangeCommand.Execute(null));
        if (args.Contains("enc-run-enable"))
        {
            enc.NewPassword = enc.ConfirmPassword = PREVIEW_MASTER_PASSWORD;
            RunAfterLoaded(() => enc.EnableCommand.Execute(null));
        }

        if (args.Contains("enc-run-disable")) RunAfterLoaded(() => enc.DisableCommand.Execute(null));
        if (args.Contains("enc-run-resume")) RunAfterLoaded(() => enc.ResumeCommand.Execute(null));
        // Saving Steam passwords switched off: the confirmation page (enc-saveoff) or confirmed (enc-forget: really deletes
        // the saved passwords of the data folder)
        if (args.Contains("enc-saveoff") || args.Contains("enc-forget")) enc.SavePasswords = false;
        if (args.Contains("enc-forget")) RunAfterLoaded(() => enc.ForgetPasswordsCommand.Execute(null));

        void RunAfterLoaded(Action action)
        {
            view.Loaded += (_, _) => DispatcherTimer.RunOnce(action, TimeSpan.FromMilliseconds(500));
        }
    }
}
